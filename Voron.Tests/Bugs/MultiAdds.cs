﻿namespace Voron.Tests.Bugs
{
	using System;
	using System.Collections.Generic;
	using System.Diagnostics;
	using System.IO;

	using Voron.Debugging;
	using Voron.Impl;
	using Voron.Trees;

	using Xunit;

	public class MultiAdds
	{
		[Fact]
		public void SplitterIssue()
		{
			const int DocumentCount = 10;

			using (var env = new StorageEnvironment(new PureMemoryPager(), s => new PureMemoryPager(), new StorageOptions()))
			{
				var rand = new Random();
				var testBuffer = new byte[168];
				rand.NextBytes(testBuffer);

				var multiTrees = CreateTrees(env, 1, "multitree");

				for (var i = 0; i < 50; i++)
				{
					AddMultiRecords(env, multiTrees, DocumentCount, true);

					ValidateMultiRecords(env, multiTrees, DocumentCount, i + 1);
				}
			}
		}

		private void ValidateRecords(StorageEnvironment env, IEnumerable<Tree> trees, int documentCount, int i)
		{
			using (var tx = env.NewTransaction(TransactionFlags.Read))
			{
				foreach (var tree in trees)
				{
					using (var iterator = tree.Iterate(tx))
					{
						Assert.True(iterator.Seek(Slice.BeforeAllKeys));

						var count = 0;
						do
						{
							count++;
						}
						while (iterator.MoveNext());

						Assert.Equal(i * documentCount, tree.State.EntriesCount);
						Assert.Equal(i * documentCount, count);
					}
				}
			}
		}

		private void ValidateMultiRecords(StorageEnvironment env, IEnumerable<Tree> trees, int documentCount, int i)
		{
			using (var tx = env.NewTransaction(TransactionFlags.Read))
			{
				for (var j = 0; j < 10; j++)
				{
					foreach (var tree in trees)
					{
						using (var iterator = tree.MultiRead(tx, (j % 10).ToString()))
						{
							Assert.True(iterator.Seek(Slice.BeforeAllKeys));

							var count = 0;
							do
							{
								count++;
							}
							while (iterator.MoveNext());

							Assert.Equal((i * documentCount) / 10, count);
						}
					}
				}
			}
		}

		private void AddRecords(StorageEnvironment env, IList<Tree> trees, int documentCount, byte[] testBuffer, bool sequential)
		{
			var key = Guid.NewGuid().ToString();
			var batch = new WriteBatch();

			for (int i = 0; i < documentCount; i++)
			{
				foreach (var tree in trees)
				{
					var id = sequential ? string.Format("tree_{0}_record_{1}_key_{2}", tree.Name, i, key) : Guid.NewGuid().ToString();

					batch.Add(id, new MemoryStream(testBuffer), tree.Name);
				}
			}

			env.Writer.Write(batch);
		}

		private void AddMultiRecords(StorageEnvironment env, IList<Tree> trees, int documentCount, bool sequential)
		{
			var key = Guid.NewGuid().ToString();
			var batch = new WriteBatch();

			for (int i = 0; i < documentCount; i++)
			{
				foreach (var tree in trees)
				{
					var value = sequential ? string.Format("tree_{0}_record_{1}_key_{2}", tree.Name, i, key) : Guid.NewGuid().ToString();

					batch.MultiAdd((i % 10).ToString(), value, tree.Name);
				}
			}

			env.Writer.Write(batch);
		}

		private IList<Tree> CreateTrees(StorageEnvironment env, int number, string prefix)
		{
			var results = new List<Tree>();

			using (var tx = env.NewTransaction(TransactionFlags.ReadWrite))
			{
				for (var i = 0; i < number; i++)
				{
					results.Add(env.CreateTree(tx, prefix + i));
				}

				tx.Commit();
			}

			return results;
		}
	}
}