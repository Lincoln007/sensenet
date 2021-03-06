﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Lucene.Net.Analysis;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.Store;
using SenseNet.Diagnostics;
using SenseNet.ContentRepository.Storage;
using SenseNet.ContentRepository.Storage.Schema;
using SenseNet.ContentRepository.Storage.Search;
using Lucene.Net.Search;
using Lucene.Net.Util;
using SenseNet.Search.Indexing.Activities;
using SenseNet.ContentRepository;
using SenseNet.Communication.Messaging;
using System.Diagnostics;
using SenseNet.ContentRepository.Storage.Data;

namespace SenseNet.Search.Indexing
{
    public class DocumentPopulator : IIndexPopulator
    {
        private class DocumentPopulatorData
        {
            internal Node Node { get; set; }
            internal NodeHead NodeHead { get; set; }
            internal NodeSaveSettings Settings { get; set; }
            internal string OriginalPath { get; set; }
            internal string NewPath { get; set; }
            internal bool IsNewNode { get; set; }
        }
        private class DeleteVersionPopulatorData
        {
            internal Node OldVersion { get; set; }
            internal Node LastDraftAfterDelete { get; set; }
        }

        /*======================================================================================================= IIndexPopulator Members */

        // caller: IndexPopulator.Populator, Import.Importer, Tests.Initializer, RunOnce
        public void ClearAndPopulateAll(bool backup = true)
        {
            var lastActivityId = LuceneManager.GetLastStoredIndexingActivityId();
            var commitData = CompletionState.GetCommitUserData(lastActivityId);

            using (var op = SnTrace.Index.StartOperation("IndexPopulator ClearAndPopulateAll"))
            {
                // recreate
                var writer = IndexManager.GetIndexWriter(true);
                try
                {
                    var excludedNodeTypes = LuceneManager.GetNotIndexedNodeTypes();
                    foreach (var docData in StorageContext.Search.LoadIndexDocumentsByPath("/Root", excludedNodeTypes))
                    {
                        var doc = IndexDocumentInfo.GetDocument(docData);
                        if (doc == null) // indexing disabled
                            continue;
                        writer.AddDocument(doc);
                        OnNodeIndexed(docData.Path);
                    }
                    RepositoryInstance.Instance.ConsoleWrite("  Commiting ... ");
                    writer.Commit(commitData);
                    RepositoryInstance.Instance.ConsoleWriteLine("ok");
                    RepositoryInstance.Instance.ConsoleWrite("  Optimizing ... ");
                    writer.Optimize();
                    RepositoryInstance.Instance.ConsoleWriteLine("ok");
                }
                finally
                {
                    writer.Close();
                }
                RepositoryInstance.Instance.ConsoleWrite("  Deleting indexing activities ... ");
                LuceneManager.DeleteAllIndexingActivities();
                RepositoryInstance.Instance.ConsoleWriteLine("ok");
                if (backup)
                {
                    RepositoryInstance.Instance.ConsoleWrite("  Making backup ... ");
                    BackupTools.BackupIndexImmediatelly();
                    RepositoryInstance.Instance.ConsoleWriteLine("ok");
                }
                op.Successful = true;
            }
        }

        // caller: IndexPopulator.Populator
        public void RepopulateTree(string path)
        {
            using (var op = SnTrace.Index.StartOperation("IndexPopulator RepopulateTree"))
            {
                var writer = IndexManager.GetIndexWriter(false);
                writer.DeleteDocuments(new Term(LucObject.FieldName.InTree, path.ToLowerInvariant()));
                try
                {
                    var excludedNodeTypes = LuceneManager.GetNotIndexedNodeTypes();
                    foreach (var docData in StorageContext.Search.LoadIndexDocumentsByPath(path, excludedNodeTypes))
                    {
                        var doc = IndexDocumentInfo.GetDocument(docData);
                        if (doc == null) // indexing disabled
                            continue;
                        writer.AddDocument(doc);
                        OnNodeIndexed(docData.Path);
                    }
                    writer.Optimize();
                }
                finally
                {
                    writer.Close();
                }
                op.Successful = true;
            }
        }

        // caller: CommitPopulateNode (rename), Node.MoveTo, Node.MoveMoreInternal
        public void PopulateTree(string path, int nodeId)
        {
            // add new tree
            CreateTreeActivityAndExecute(IndexingActivityType.AddTree, path, nodeId, false, null);
        }

        // caller: Node.Save, Node.SaveCopied
        public object BeginPopulateNode(Node node, NodeSaveSettings settings, string originalPath, string newPath)
        {
            var populatorData = new DocumentPopulatorData
            {
                Node = node,
                Settings = settings,
                OriginalPath = originalPath,
                NewPath = newPath,
                NodeHead = settings.NodeHead,
                IsNewNode = node.Id == 0,
            };
            return populatorData;
        }
        public void CommitPopulateNode(object data, IndexDocumentData indexDocument = null)
        {
            var state = (DocumentPopulatorData)data;
            var versioningInfo = GetVersioningInfo(state);

            var settings = state.Settings;

            using (var op = SnTrace.Index.StartOperation("DocumentPopulator.CommitPopulateNode. Version: {0}, VersionId: {1}, Path: {2}", state.Node.Version, state.Node.VersionId, state.Node.Path))
            {
                if (!state.OriginalPath.Equals(state.NewPath, StringComparison.InvariantCultureIgnoreCase))
                {
                    DeleteTree(state.OriginalPath, state.Node.Id, true);
                    PopulateTree(state.NewPath, state.Node.Id);
                }
                else if (state.IsNewNode)
                {
                    CreateBrandNewNode(state.Node, versioningInfo, indexDocument);
                }
                else if (state.Settings.IsNewVersion())
                {
                    AddNewVersion(state.Node, versioningInfo, indexDocument);
                }
                else
                {
                    UpdateVersion(state, versioningInfo, indexDocument);
                }

                OnNodeIndexed(state.Node.Path);

                op.Successful = true;
            }
        }
        public void FinalizeTextExtracting(object data, IndexDocumentData indexDocument)
        {
            var state = (DocumentPopulatorData)data;
            var versioningInfo = GetVersioningInfo(state);
            UpdateVersion(state, versioningInfo, indexDocument);
        }

        private VersioningInfo GetVersioningInfo(DocumentPopulatorData state)
        {
            var settings = state.Settings;

            var mustReindex = new List<int>();
            if (settings.LastMajorVersionIdBefore != settings.LastMajorVersionIdAfter)
            {
                mustReindex.Add(settings.LastMajorVersionIdBefore);
                mustReindex.Add(settings.LastMajorVersionIdAfter);
            }
            if (settings.LastMinorVersionIdBefore != settings.LastMinorVersionIdAfter)
            {
                mustReindex.Add(settings.LastMinorVersionIdBefore);
                mustReindex.Add(settings.LastMinorVersionIdAfter);
            }

            return new VersioningInfo
            {
                LastDraftVersionId = settings.LastMinorVersionIdAfter,
                LastPublicVersionId = settings.LastMajorVersionIdAfter,
                Delete = settings.DeletableVersionIds.ToArray(),
                Reindex = mustReindex.Except(new[] { 0, state.Node.VersionId }).Except(settings.DeletableVersionIds).ToArray()
            };
        }

        // caller: CommitPopulateNode (rename), Node.MoveTo, Node.ForceDelete
        public void DeleteTree(string path, int nodeId, bool moveOrRename)
        {
            // add new tree
            CreateTreeActivityAndExecute(IndexingActivityType.RemoveTree, path, nodeId, moveOrRename, null);
        }

        // caller: Node.DeleteMoreInternal
        public void DeleteForest(IEnumerable<Int32> idSet, bool moveOrRename)
        {
            foreach (var head in NodeHead.Get(idSet))
                DeleteTree(head.Path, head.Id, moveOrRename);
        }
        // caller: Node.MoveMoreInternal
        public void DeleteForest(IEnumerable<string> pathSet, bool moveOrRename)
        {
            foreach (var head in NodeHead.Get(pathSet))
                DeleteTree(head.Path, head.Id, moveOrRename);
        }

        #region Obsolete API
        [Obsolete("This API is prohibited due to the poor performance. Use RebuildIndex method instead.", true)]
        public void RefreshIndexDocumentInfo(IEnumerable<Node> nodes)
        {
        }
        [Obsolete("This API is prohibited due to the poor performance. Use RebuildIndex method instead.", true)]
        public void RefreshIndexDocumentInfo(Node node, bool recursive)
        {
        }
        [Obsolete("This API is prohibited due to the poor performance. Use RebuildIndex method instead.", true)]
        public void RefreshIndex(IEnumerable<Node> nodes)
        {
        }
        [Obsolete("This API is prohibited due to the poor performance. Use RebuildIndex method instead.", true)]
        public void RefreshIndex(Node node, bool recursive)
        {
        }
        #endregion

        public void RebuildIndex(Node node, bool recursive = false, IndexRebuildLevel rebuildLevel = IndexRebuildLevel.IndexOnly)
        {
            using (var op = SnTrace.Index.StartOperation("DocumentPopulator.RefreshIndex. Version: {0}, VersionId: {1}, recursive: {2}, level: {3}", node.Version, node.VersionId, recursive, rebuildLevel))
            {
                using (new SenseNet.ContentRepository.Storage.Security.SystemAccount())
                {
                    var databaseAndIndex = rebuildLevel == IndexRebuildLevel.DatabaseAndIndex;
                    if (recursive)
                        RebuildIndex_Recursive(node, databaseAndIndex);
                    else
                        RebuildIndex_NoRecursive(node, databaseAndIndex);
                }
                op.Successful = true;
            }
        }
        private void RebuildIndex_NoRecursive(Node node, bool databaseAndIndex)
        {
            TreeLock.AssertFree(node.Path);

            var head = NodeHead.Get(node.Id);
            bool hasBinary;
            if (databaseAndIndex)
                foreach (var version in head.Versions.Select(v => Node.LoadNodeByVersionId(v.VersionId)))
                    DataBackingStore.SaveIndexDocument(version, false, false, out hasBinary);

            var versioningInfo = new VersioningInfo
            {
                LastDraftVersionId = head.LastMinorVersionId,
                LastPublicVersionId = head.LastMajorVersionId,
                Delete = new int[0],
                Reindex = new int[0]
            };

            CreateActivityAndExecute(IndexingActivityType.Rebuild, node.Path, node.Id, 0, 0, null, versioningInfo, null);
        }

        private void RebuildIndex_Recursive(Node node, bool databaseAndIndex)
        {
            bool hasBinary;
            using (TreeLock.Acquire(node.Path))
            {
                DeleteTree(node.Path, node.Id, true);
                if (databaseAndIndex)
                    foreach (var n in NodeQuery.QueryNodesByPath(node.Path, true).Nodes)
                        DataBackingStore.SaveIndexDocument(n, false, false, out hasBinary);
                PopulateTree(node.Path, node.Id);
            }
        }


        public event EventHandler<NodeIndexedEvenArgs> NodeIndexed;
        protected void OnNodeIndexed(string path)
        {
            if (NodeIndexed == null)
                return;
            NodeIndexed(null, new NodeIndexedEvenArgs(path));
        }


        /*================================================================================================================================*/

        // caller: CommitPopulateNode
        private static void CreateBrandNewNode(Node node, VersioningInfo versioningInfo, IndexDocumentData indexDocumentData)
        {
            CreateActivityAndExecute(IndexingActivityType.AddDocument, node.Path, node.Id, node.VersionId, node.VersionTimestamp, true, versioningInfo, indexDocumentData);
        }
        // caller: CommitPopulateNode
        private static void AddNewVersion(Node newVersion, VersioningInfo versioningInfo, IndexDocumentData indexDocumentData)
        {
            CreateActivityAndExecute(IndexingActivityType.AddDocument, newVersion.Path, newVersion.Id, newVersion.VersionId, newVersion.VersionTimestamp, null, versioningInfo, indexDocumentData);
        }
        // caller: CommitPopulateNode
        private static void UpdateVersion(DocumentPopulatorData state, VersioningInfo versioningInfo, IndexDocumentData indexDocumentData)
        {
            CreateActivityAndExecute(IndexingActivityType.UpdateDocument, state.OriginalPath, state.Node.Id, state.Node.VersionId, state.Node.VersionTimestamp, null, versioningInfo, indexDocumentData);
        }

        // caller: ClearAndPopulateAll, RepopulateTree
        private static IEnumerable<Node> GetVersions(Node node)
        {
            var versionNumbers = Node.GetVersionNumbers(node.Id);
            var versions = from versionNumber in versionNumbers select Node.LoadNode(node.Id, versionNumber);
            return versions.ToArray();
        }
        /*================================================================================================================================*/

        private static LuceneIndexingActivity CreateActivity(IndexingActivityType type, string path, int nodeId, int versionId, long versionTimestamp, bool? singleVersion, VersioningInfo versioningInfo, IndexDocumentData indexDocumentData)
        {
            var activity = (LuceneIndexingActivity)IndexingActivityFactory.Instance.CreateActivity(type);
            activity.Path = path.ToLowerInvariant();
            activity.NodeId = nodeId;
            activity.VersionId = versionId;
            activity.VersionTimestamp = versionTimestamp;
            activity.SingleVersion = singleVersion;

            if (indexDocumentData != null)
            {
                var lucDocAct = activity as LuceneDocumentActivity;
                if (lucDocAct != null)
                    lucDocAct.IndexDocumentData = indexDocumentData;
            }

            var documentActivity = activity as LuceneDocumentActivity;
            if (documentActivity != null)
                documentActivity.Versioning = versioningInfo;

            return activity;
        }
        private static LuceneIndexingActivity CreateTreeActivity(IndexingActivityType type, string path, int nodeId, bool moveOrRename, IndexDocumentData indexDocumentData)
        {
            var activity = (LuceneIndexingActivity)IndexingActivityFactory.Instance.CreateActivity(type);
            activity.Path = path.ToLowerInvariant();
            activity.NodeId = nodeId;
            activity.MoveOrRename = moveOrRename;

            if (indexDocumentData != null)
            {
                var lucDocAct = activity as LuceneDocumentActivity;
                if (lucDocAct != null)
                    lucDocAct.IndexDocumentData = indexDocumentData;
            }

            return activity;
        }
        private static void CreateActivityAndExecute(IndexingActivityType type, string path, int nodeId, int versionId, long versionTimestamp, bool? singleVersion, VersioningInfo versioningInfo, IndexDocumentData indexDocumentData)
        {
            ExecuteActivity(CreateActivity(type, path, nodeId, versionId, versionTimestamp, singleVersion, versioningInfo, indexDocumentData));
        }
        private static void CreateTreeActivityAndExecute(IndexingActivityType type, string path, int nodeId, bool moveOrRename, IndexDocumentData indexDocumentData)
        {
            ExecuteActivity(CreateTreeActivity(type, path, nodeId, moveOrRename, indexDocumentData));
        }
        private static void ExecuteActivity(LuceneIndexingActivity activity)
        {
            LuceneManager.RegisterActivity(activity);
            LuceneManager.ExecuteActivity(activity, true, true);
        }
    }
}
