﻿// -----------------------------------------------------------------------
//  <copyright file="StorageReportGenerator.cs" company="Hibernating Rhinos LTD">
//      Copyright (c) Hibernating Rhinos LTD. All rights reserved.
//  </copyright>
// -----------------------------------------------------------------------
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using Voron.Impl;
using Voron.Impl.FileHeaders;
using Voron.Impl.Journal;
using Voron.Impl.Paging;
using Voron.Trees;
using Voron.Trees.Fixed;

namespace Voron.Debugging
{
	public class ReportInput
	{
		public long NumberOfAllocatedPages;
		public long NumberOfFreePages;
		public long NextPageNumber;
		public List<Tree> Trees;
		public List<JournalFile> Journals;
		public bool IsLightReport { get; set; }
	}

	public unsafe class StorageReportGenerator
	{
		public class TreeDensityInput
		{
			public readonly List<double> Densities = new List<double>();
		}

		private readonly Transaction _tx;

		public StorageReportGenerator(Transaction tx)
		{
			_tx = tx;
		}

		public StorageReport Generate(ReportInput input)
		{
			var unallocatedPagesAtEndOfFile = input.NumberOfAllocatedPages - input.NextPageNumber;

			var dataFile = new DataFileReport
			{
				AllocatedSpaceInBytes = PagesToBytes(input.NumberOfAllocatedPages),
				SpaceInUseInBytes = PagesToBytes(input.NextPageNumber - input.NumberOfFreePages),
				FreeSpaceInBytes = PagesToBytes(input.NumberOfFreePages + unallocatedPagesAtEndOfFile)
			};

			var trees = new List<TreeReport>();

			foreach (var tree in input.Trees)
			{
				List<double> densities = null;

				if (input.IsLightReport == false)
				{
					densities = GetPageDensities(tree);
				}

				MultiValuesReport multiValues = null;

				if (tree.State.Flags == TreeFlags.MultiValueTrees)
				{
					multiValues = CreateMultiValuesReport(tree);
				}

				var state = tree.State;
				var treeReport = new TreeReport
				{
					Name = tree.Name,
					BranchPages = state.BranchPages,
					Depth = state.Depth,
					EntriesCount = state.EntriesCount,
					LeafPages = state.LeafPages,
					OverflowPages = state.OverflowPages,
					PageCount = state.PageCount,
					Density = densities == null ? 0 : CalculateTreeDensity(densities),
					MultiValues = multiValues
				};

				trees.Add(treeReport);
			}

			var journals = input.Journals.Select(journal => new JournalReport
			{
				Number = journal.Number,
				AllocatedSpaceInBytes = PagesToBytes(journal.JournalWriter.NumberOfAllocatedPages)
			}).ToList();

			return new StorageReport
			{
				DataFile = dataFile,
				Trees = trees,
				Journals = journals
			};
		}

		private MultiValuesReport CreateMultiValuesReport(Tree tree)
		{
			var multiValues = new MultiValuesReport();

			using (var multiTreeIterator = tree.Iterate())
			{
				if (multiTreeIterator.Seek(Slice.BeforeAllKeys))
				{
					do
					{
						var currentNode = multiTreeIterator.Current;

						switch (currentNode->Flags)
						{
							case TreeNodeFlags.MultiValuePageRef:
							{
								var multiValueTreeHeader = (TreeRootHeader*) ((byte*) currentNode + currentNode->KeySize + Constants.NodeHeaderSize);

								Debug.Assert(multiValueTreeHeader->Flags == TreeFlags.MultiValue);

								multiValues.EntriesCount += multiValueTreeHeader->EntriesCount;
								multiValues.BranchPages += multiValueTreeHeader->BranchPages;
								multiValues.LeafPages += multiValueTreeHeader->LeafPages;
								multiValues.PageCount += multiValueTreeHeader->PageCount;
								break;
							}
							case TreeNodeFlags.Data:
							{
								var nestedPage = GetNestedMultiValuePage(TreeNodeHeader.DirectAccess(_tx, currentNode), currentNode);

								multiValues.EntriesCount += nestedPage.NumberOfEntries;
								break;
							}
							case TreeNodeFlags.PageRef:
							{
								var overFlowPage = _tx.GetReadOnlyPage(currentNode->PageNumber);
								var nestedPage = GetNestedMultiValuePage(overFlowPage.Base + Constants.PageHeaderSize, currentNode);

								multiValues.EntriesCount += nestedPage.NumberOfEntries;
								break;
							}
							default:
								throw new InvalidEnumArgumentException("currentNode->Flags", (int) currentNode->Flags, typeof (TreeNodeFlags));
						}
					} while (multiTreeIterator.MoveNext());
				}
			}
			return multiValues;
		}

		private List<double> GetPageDensities(Tree tree)
		{
			var densities = new List<double>();
			var allPages = tree.AllPages();
			var pageSize = tree.Tx.Environment.Options.PageSize;

			for (var i = 0; i < allPages.Count; i++)
			{
				var page = _tx.GetReadOnlyPage(allPages[i]);

				if (page.IsOverflow)
				{
					var numberOfPages = _tx.DataPager.GetNumberOfOverflowPages(page.OverflowSize);

					densities.Add(((double)(page.OverflowSize + Constants.PageHeaderSize)) / (numberOfPages * pageSize));

					i += (numberOfPages - 1);
				}
				else
				{
					if (page.IsFixedSize)
					{
						var sizeUsed = Constants.PageHeaderSize + (page.FixedSize_NumberOfEntries*(page.IsLeaf ? page.FixedSize_ValueSize : FixedSizeTree.BranchEntrySize));
						densities.Add(((double) sizeUsed)/pageSize);
					}
					else
					{
						densities.Add(((double)page.SizeUsed) / pageSize);
					}
				}
			}
			return densities;
		}

		private TreePage GetNestedMultiValuePage(byte* nestedPagePtr, TreeNodeHeader* currentNode)
		{
			var nestedPage = new TreePage(nestedPagePtr, "multi tree", (ushort) TreeNodeHeader.GetDataSize(_tx, currentNode));

			Debug.Assert(nestedPage.PageNumber == -1); // nested page marker
			return nestedPage;
		}

		private long PagesToBytes(long pageCount)
		{
			return pageCount * _tx.Environment.Options.PageSize;
		}

		public static double CalculateTreeDensity(List<double> pageDensities)
		{
			return pageDensities.Sum(x => x) / pageDensities.Count;
		}
	}
}
