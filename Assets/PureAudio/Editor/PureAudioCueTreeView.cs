using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

namespace PureAudio.Editor
{
    public class PureAudioTreeViewItem : TreeViewItem
    {
        public bool isDirectory;
        public string relativePath; // "Assets/PureAudio/Resources/PureAudio/Cues/..." からの相対または絶対パス
        public PureAudioCue cueAsset;

        public PureAudioTreeViewItem(int id, int depth, string displayName, bool isDir, string path, PureAudioCue asset = null)
            : base(id, depth, displayName)
        {
            this.isDirectory = isDir;
            this.relativePath = path;
            this.cueAsset = asset;
            
            // アイコンの設定
            if (isDir)
            {
                this.icon = EditorGUIUtility.FindTexture("Folder Icon");
            }
            else
            {
                this.icon = EditorGUIUtility.FindTexture("AudioSource Icon");
            }
        }
    }

    public class PureAudioCueTreeView : TreeView
    {
        private readonly string _basePath = "Assets/PureAudio/Resources/PureAudio/Cues";
        private List<TreeViewItem> _allItems = new List<TreeViewItem>();
        public event Action<List<PureAudioCue>> OnSelectionChanged;

        private Dictionary<string, int> _pathToIdMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private int _nextId = 1;

        public int GetOrCreateIdForPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return 0;
            string key = path.Replace('\\', '/').ToLowerInvariant();
            if (!_pathToIdMap.TryGetValue(key, out int id))
            {
                id = _nextId++;
                _pathToIdMap[key] = id;
            }
            return id;
        }

        public static int GetPathHash(string path)
        {
            if (string.IsNullOrEmpty(path)) return 0;
            return path.Replace('\\', '/').ToLowerInvariant().GetHashCode();
        }

        public PureAudioCueTreeView(TreeViewState state, MultiColumnHeader header) : base(state, header)
        {
            showBorder = true;
            showAlternatingRowBackgrounds = true;
            header.sortedColumnIndex = 0;
            header.sortingChanged += OnSortingChanged;
            Reload();
        }

        private void OnSortingChanged(MultiColumnHeader multiColumnHeader)
        {
            SortRows(multiColumnHeader);
        }

        private Dictionary<string, int> _cueInspectorCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, int> _cueScriptCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, List<string>> _cueReferencePaths = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        public int GetReferenceCount(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return 0;
            string guid = AssetDatabase.AssetPathToGUID(assetPath);
            if (string.IsNullOrEmpty(guid)) return 0;

            int inspectorCount = 0;
            int scriptCount = 0;
            _cueInspectorCounts.TryGetValue(guid, out inspectorCount);
            _cueScriptCounts.TryGetValue(guid, out scriptCount);
            return inspectorCount + scriptCount;
        }

        public void GetReferenceDetails(string assetPath, out int inspectorCount, out int scriptCount, out List<string> refPaths)
        {
            inspectorCount = 0;
            scriptCount = 0;
            refPaths = new List<string>();

            if (string.IsNullOrEmpty(assetPath)) return;
            string guid = AssetDatabase.AssetPathToGUID(assetPath);
            if (string.IsNullOrEmpty(guid)) return;

            _cueInspectorCounts.TryGetValue(guid, out inspectorCount);
            _cueScriptCounts.TryGetValue(guid, out scriptCount);
            if (_cueReferencePaths.TryGetValue(guid, out var paths))
            {
                refPaths = new List<string>(paths);
            }
        }

        private void CalculateCueReferenceCounts()
        {
            _cueInspectorCounts.Clear();
            _cueScriptCounts.Clear();
            _cueReferencePaths.Clear();

            string[] cueGuids = AssetDatabase.FindAssets("t:PureAudioCue");
            var cueGuidToPath = new Dictionary<string, string>();
            var cueNameToGuid = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var guid in cueGuids)
            {
                string p = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.IsNullOrEmpty(p))
                {
                    cueGuidToPath[guid] = p;
                    _cueInspectorCounts[guid] = 0;
                    _cueScriptCounts[guid] = 0;
                    _cueReferencePaths[guid] = new List<string>();

                    string cueName = Path.GetFileNameWithoutExtension(p);
                    cueNameToGuid[cueName] = guid;
                }
            }

            if (cueGuidToPath.Count == 0) return;

            // 1. インスペクター参照スキャン (Prefab, Scene, SO)
            string[] searchGuids = AssetDatabase.FindAssets("t:Prefab t:Scene t:ScriptableObject t:MonoBehaviour");
            var uniqueSearchPaths = new HashSet<string>();
            foreach (var guid in searchGuids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.IsNullOrEmpty(path) && !path.StartsWith("Packages/") && !path.Contains("PureAudio/Resources/PureAudio/Cues"))
                {
                    uniqueSearchPaths.Add(path);
                }
            }

            foreach (var path in uniqueSearchPaths)
            {
                if (!File.Exists(path)) continue;

                try
                {
                    string text = File.ReadAllText(path);
                    foreach (var pair in cueGuidToPath)
                    {
                        if (text.Contains(pair.Key))
                        {
                            _cueInspectorCounts[pair.Key]++;
                            if (!_cueReferencePaths[pair.Key].Contains(path))
                            {
                                _cueReferencePaths[pair.Key].Add(path);
                            }
                        }
                    }
                }
                catch (Exception)
                {
                    // Ignore read errors
                }
            }

            // 2. C#スクリプト参照スキャン (.csファイル内文字列検索)
            string dataPath = Application.dataPath;
            if (Directory.Exists(dataPath))
            {
                string[] csFiles = Directory.GetFiles(dataPath, "*.cs", SearchOption.AllDirectories);
                var uniqueCsPaths = new List<string>();
                foreach (var file in csFiles)
                {
                    string normPath = file.Replace('\\', '/');
                    if (!normPath.Contains("/PureAudio/"))
                    {
                        string relativePath = "Assets" + normPath.Substring(dataPath.Length);
                        uniqueCsPaths.Add(relativePath);
                    }
                }

                foreach (var path in uniqueCsPaths)
                {
                    if (!File.Exists(path)) continue;

                    try
                    {
                        string text = File.ReadAllText(path);
                        foreach (var pair in cueNameToGuid)
                        {
                            string cueName = pair.Key;
                            string guid = pair.Value;
                            string searchString = "\"" + cueName + "\"";
                            if (text.Contains(searchString))
                            {
                                int count = 0;
                                int minIndex = text.IndexOf(searchString, StringComparison.Ordinal);
                                while (minIndex != -1)
                                {
                                    count++;
                                    minIndex = text.IndexOf(searchString, minIndex + searchString.Length, StringComparison.Ordinal);
                                }

                                if (count > 0)
                                {
                                    _cueScriptCounts[guid] += count;
                                    if (!_cueReferencePaths[guid].Contains(path))
                                    {
                                        _cueReferencePaths[guid].Add(path);
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception)
                    {
                        // Ignore read errors
                    }
                }
            }
        }

        public int GetAssetCountRecursive(TreeViewItem item)
        {
            int count = 0;
            if (item.children == null) return count;

            foreach (var child in item.children)
            {
                var customChild = child as PureAudioTreeViewItem;
                if (customChild != null)
                {
                    if (!customChild.isDirectory)
                    {
                        count++;
                    }
                    else
                    {
                        count += GetAssetCountRecursive(customChild);
                    }
                }
            }
            return count;
        }

        protected override bool CanRename(TreeViewItem item)
        {
            var customItem = item as PureAudioTreeViewItem;
            return customItem != null && customItem.isDirectory;
        }

        protected override void RenameEnded(RenameEndedArgs args)
        {
            if (args.acceptedRename && !string.IsNullOrEmpty(args.newName))
            {
                var item = GetItem(args.itemID);
                if (item != null && item.isDirectory)
                {
                    string oldPath = item.relativePath;
                    string parentDir = Path.GetDirectoryName(oldPath).Replace('\\', '/');
                    string newPath = Path.Combine(parentDir, args.newName).Replace('\\', '/');

                    if (oldPath != newPath && Directory.Exists(oldPath))
                    {
                        if (Directory.Exists(newPath))
                        {
                            Debug.LogWarning($"[PureAudio] Rename failed. Folder already exists at path: {newPath}");
                            return;
                        }

                        try
                        {
                            Directory.Move(oldPath, newPath);
                            string oldMeta = oldPath + ".meta";
                            string newMeta = newPath + ".meta";
                            if (File.Exists(oldMeta))
                            {
                                File.Move(oldMeta, newMeta);
                            }

                            AssetDatabase.Refresh();
                            Reload();
                        }
                        catch (Exception e)
                        {
                            Debug.LogError($"[PureAudio] Failed to rename folder: {e.Message}");
                        }
                    }
                }
            }
        }

        protected override void DoubleClickedItem(int id)
        {
            var item = GetItem(id);
            if (item != null && item.isDirectory)
            {
                BeginRename(item);
            }
        }

        protected override void KeyEvent()
        {
            if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.F2)
            {
                var selection = GetSelection();
                if (selection.Count > 0)
                {
                    var item = GetItem(selection[0]);
                    if (item != null && item.isDirectory)
                    {
                        BeginRename(item);
                    }
                }
            }
        }

        protected override TreeViewItem BuildRoot()
        {
            CalculateCueReferenceCounts();
            var root = new TreeViewItem { id = 0, depth = -1, displayName = "Root" };

            _allItems.Clear();

            // 1. アセットとディレクトリの走査
            EnsureDirectoryExists();
            
            // すべてのCueアセットを取得
            string[] guids = AssetDatabase.FindAssets("t:PureAudioCue", new[] { _basePath });
            var cuePaths = guids.Select(AssetDatabase.GUIDToAssetPath).Distinct().ToList();

            // ツリー構築のためのマップ
            var pathNodeMap = new Dictionary<string, PureAudioTreeViewItem>(StringComparer.OrdinalIgnoreCase);

            foreach (var path in cuePaths)
            {
                // _basePath からの相対的なパス構成を取得
                string relative = path.Substring(_basePath.Length).TrimStart('/', '\\');
                string[] parts = relative.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);

                string currentAccumulatedPath = _basePath;
                PureAudioTreeViewItem parent = null;

                for (int i = 0; i < parts.Length; i++)
                {
                    string part = parts[i];
                    currentAccumulatedPath = Path.Combine(currentAccumulatedPath, part).Replace('\\', '/');
                    string keyPath = currentAccumulatedPath.ToLowerInvariant();
                    bool isDir = i < parts.Length - 1;

                    int id = GetOrCreateIdForPath(currentAccumulatedPath);

                    if (!pathNodeMap.TryGetValue(keyPath, out var node))
                    {
                        if (isDir)
                        {
                            node = new PureAudioTreeViewItem(id, i, part, true, currentAccumulatedPath);
                        }
                        else
                        {
                            var asset = AssetDatabase.LoadAssetAtPath<PureAudioCue>(path);
                            node = new PureAudioTreeViewItem(id, i, Path.GetFileNameWithoutExtension(part), false, currentAccumulatedPath, asset);
                        }

                        pathNodeMap[keyPath] = node;
                        _allItems.Add(node);

                        if (parent == null)
                        {
                            root.AddChild(node);
                        }
                        else
                        {
                            parent.AddChild(node);
                        }
                    }
                    
                    parent = node;
                }
            }

            // 子ノードを持たない空のフォルダも物理的に存在する場合は検知して追加
            AddEmptyFolders(root, _basePath, pathNodeMap);

            // ソート適用
            if (multiColumnHeader.sortedColumnIndex >= 0)
            {
                SortTreeNodes(root, multiColumnHeader);
            }

            // TreeView内部に行リストを再構築させるための深さ設定
            SetupDepthsFromParentsAndChildren(root);

            return root;
        }

        protected override IList<TreeViewItem> BuildRows(TreeViewItem root)
        {
            var rows = new List<TreeViewItem>();

            if (!string.IsNullOrEmpty(searchString))
            {
                AddFilteredRows(root, rows, searchString);
            }
            else
            {
                AddExpandedRows(root, rows);
            }

            return rows;
        }

        private void AddFilteredRows(TreeViewItem item, IList<TreeViewItem> rows, string search)
        {
            if (item.children == null) return;

            foreach (var child in item.children)
            {
                var customChild = child as PureAudioTreeViewItem;
                if (customChild != null)
                {
                    if (IsMatchOrHasMatchingChild(customChild, search))
                    {
                        rows.Add(child);
                        if (child.hasChildren)
                        {
                            AddFilteredRows(child, rows, search);
                        }
                    }
                }
            }
        }

        private bool IsMatchOrHasMatchingChild(PureAudioTreeViewItem item, string search)
        {
            if (item == null) return false;

            if (!item.isDirectory)
            {
                bool matchesCueName = item.displayName.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;
                bool matchesCategory = item.cueAsset != null && 
                                       item.cueAsset.category != null && 
                                       item.cueAsset.category.categoryName.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;
                if (matchesCueName || matchesCategory) return true;
            }
            else
            {
                if (item.displayName.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            if (item.children != null)
            {
                foreach (var child in item.children)
                {
                    if (IsMatchOrHasMatchingChild(child as PureAudioTreeViewItem, search))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private void AddEmptyFolders(TreeViewItem root, string currentPath, Dictionary<string, PureAudioTreeViewItem> pathNodeMap)
        {
            if (!Directory.Exists(currentPath)) return;

            string[] subDirs = Directory.GetDirectories(currentPath);
            foreach (var dir in subDirs)
            {
                string cleanDir = dir.Replace('\\', '/');
                string keyPath = cleanDir.ToLowerInvariant();
                int depth = cleanDir.Substring(_basePath.Length).Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries).Length - 1;
                int id = GetOrCreateIdForPath(cleanDir);

                if (!pathNodeMap.TryGetValue(keyPath, out var node))
                {
                    string folderName = Path.GetFileName(cleanDir);
                    node = new PureAudioTreeViewItem(id, depth, folderName, true, cleanDir);
                    pathNodeMap[keyPath] = node;
                    _allItems.Add(node);

                    // 親を探して追加
                    string parentDir = Path.GetDirectoryName(cleanDir).Replace('\\', '/');
                    string parentKey = parentDir.ToLowerInvariant();
                    if (pathNodeMap.TryGetValue(parentKey, out var parentNode))
                    {
                        parentNode.AddChild(node);
                    }
                    else
                    {
                        root.AddChild(node);
                    }
                }

                // 再帰
                AddEmptyFolders(root, cleanDir, pathNodeMap);
            }
        }

        private void SortRows(MultiColumnHeader header)
        {
            Reload();
        }

        private void SortTreeNodes(TreeViewItem root, MultiColumnHeader header)
        {
            SortNodeChildrenRecursive(root, header);
        }

        private void SortNodeChildrenRecursive(TreeViewItem node, MultiColumnHeader header)
        {
            if (node.children == null) return;

            int col = header.sortedColumnIndex;
            bool ascii = header.IsSortedAscending(col);

            node.children.Sort((x, y) =>
            {
                var ax = x as PureAudioTreeViewItem;
                var ay = y as PureAudioTreeViewItem;

                if (ax == null || ay == null) return 0;

                // フォルダを常に上に配置
                if (ax.isDirectory && !ay.isDirectory) return -1;
                if (!ax.isDirectory && ay.isDirectory) return 1;

                int result = 0;
                switch (col)
                {
                    case 0: // Name
                        result = string.Compare(ax.displayName, ay.displayName, StringComparison.OrdinalIgnoreCase);
                        break;
                    case 1: // Category
                        string catX = (ax.cueAsset != null && ax.cueAsset.category != null) ? ax.cueAsset.category.categoryName : "";
                        string catY = (ay.cueAsset != null && ay.cueAsset.category != null) ? ay.cueAsset.category.categoryName : "";
                        result = string.Compare(catX, catY, StringComparison.OrdinalIgnoreCase);
                        break;
                    case 2: // Type
                        string typX = ax.cueAsset != null ? ax.cueAsset.playbackType.ToString() : "";
                        string typY = ay.cueAsset != null ? ay.cueAsset.playbackType.ToString() : "";
                        result = string.Compare(typX, typY, StringComparison.OrdinalIgnoreCase);
                        break;
                    case 3: // Volume
                        float volX = ax.cueAsset != null ? ax.cueAsset.volume : 0f;
                        float volY = ay.cueAsset != null ? ay.cueAsset.volume : 0f;
                        result = volX.CompareTo(volY);
                        break;
                    case 4: // Pitch
                        float pitX = ax.cueAsset != null ? ax.cueAsset.pitch : 0f;
                        float pitY = ay.cueAsset != null ? ay.cueAsset.pitch : 0f;
                        result = pitX.CompareTo(pitY);
                        break;
                    case 5: // Max Voices
                        int mvX = ax.cueAsset != null ? ax.cueAsset.maxVoices : 0;
                        int mvY = ay.cueAsset != null ? ay.cueAsset.maxVoices : 0;
                        result = mvX.CompareTo(mvY);
                        break;
                    case 6: // Spatial
                        float spX = ax.cueAsset != null ? ax.cueAsset.spatialBlend : 0f;
                        float spY = ay.cueAsset != null ? ay.cueAsset.spatialBlend : 0f;
                        result = spX.CompareTo(spY);
                        break;
                    case 7: // Loop
                        bool lpX = ax.cueAsset != null && ax.cueAsset.isLooping;
                        bool lpY = ay.cueAsset != null && ay.cueAsset.isLooping;
                        result = lpX.CompareTo(lpY);
                        break;
                    case 8: // Addressable
                        bool adX = ax.cueAsset != null && ax.cueAsset.isAddressable;
                        bool adY = ay.cueAsset != null && ay.cueAsset.isAddressable;
                        result = adX.CompareTo(adY);
                        break;
                    case 9: // References
                        int refX = !ax.isDirectory ? GetReferenceCount(ax.relativePath) : 0;
                        int refY = !ay.isDirectory ? GetReferenceCount(ay.relativePath) : 0;
                        result = refX.CompareTo(refY);
                        break;
                }

                return ascii ? result : -result;
            });

            foreach (var child in node.children)
            {
                SortNodeChildrenRecursive(child, header);
            }
        }

        private void AddExpandedRows(TreeViewItem item, IList<TreeViewItem> rows)
        {
            if (item.children == null) return;

            foreach (var child in item.children)
            {
                rows.Add(child);
                if (child.hasChildren && IsExpanded(child.id))
                {
                    AddExpandedRows(child, rows);
                }
            }
        }

        protected override void RowGUI(RowGUIArgs args)
        {
            var item = args.item as PureAudioTreeViewItem;
            if (item == null) return;

            for (int i = 0; i < args.GetNumVisibleColumns(); ++i)
            {
                CellGUI(args.GetCellRect(i), item, args.GetColumn(i), ref args);
            }
        }

        private void CellGUI(Rect cellRect, PureAudioTreeViewItem item, int column, ref RowGUIArgs args)
        {
            CenterRectUsingSingleLineHeight(ref cellRect);

            switch (column)
            {
                case 0: // Name (インデント表現とアイコンを含む)
                    float indent = GetContentIndent(item);
                    Rect labelRect = new Rect(cellRect.x + indent, cellRect.y, cellRect.width - indent, cellRect.height);

                    // アイコンの描画
                    if (item.icon != null)
                    {
                        Rect iconRect = new Rect(labelRect.x, labelRect.y, 16, 16);
                        GUI.DrawTexture(iconRect, item.icon);
                        labelRect.xMin += 20;
                    }

                    args.rowRect = cellRect;
                    string nameText = item.displayName;
                    if (item.isDirectory)
                    {
                        int assetCount = GetAssetCountRecursive(item);
                        nameText = $"{item.displayName} ({assetCount})";
                    }
                    DefaultGUI.Label(labelRect, nameText, args.selected, args.focused);
                    break;

                case 1: // Category
                    if (!item.isDirectory && item.cueAsset != null)
                    {
                        string catName = item.cueAsset.category != null ? item.cueAsset.category.categoryName : "[No Category]";
                        DefaultGUI.Label(cellRect, catName, args.selected, args.focused);
                    }
                    break;

                case 2: // Type
                    if (!item.isDirectory && item.cueAsset != null)
                    {
                        DefaultGUI.Label(cellRect, item.cueAsset.playbackType.ToString(), args.selected, args.focused);
                    }
                    break;

                case 3: // Volume
                    if (!item.isDirectory && item.cueAsset != null)
                    {
                        DefaultGUI.Label(cellRect, item.cueAsset.volume.ToString("F2"), args.selected, args.focused);
                    }
                    break;

                case 4: // Pitch
                    if (!item.isDirectory && item.cueAsset != null)
                    {
                        DefaultGUI.Label(cellRect, item.cueAsset.pitch.ToString("F2"), args.selected, args.focused);
                    }
                    break;

                case 5: // Max Voices
                    if (!item.isDirectory && item.cueAsset != null)
                    {
                        DefaultGUI.Label(cellRect, item.cueAsset.maxVoices.ToString(), args.selected, args.focused);
                    }
                    break;

                case 6: // Spatial
                    if (!item.isDirectory && item.cueAsset != null)
                    {
                        DefaultGUI.Label(cellRect, item.cueAsset.spatialBlend.ToString("F2"), args.selected, args.focused);
                    }
                    break;

                case 7: // Loop
                    if (!item.isDirectory && item.cueAsset != null)
                    {
                        DefaultGUI.Label(cellRect, item.cueAsset.isLooping ? "Loop" : "Single", args.selected, args.focused);
                    }
                    break;

                case 8: // Addressable
                    if (!item.isDirectory && item.cueAsset != null)
                    {
                        DefaultGUI.Label(cellRect, item.cueAsset.isAddressable ? "Yes" : "No", args.selected, args.focused);
                    }
                    break;

                case 9: // References
                    if (!item.isDirectory && item.cueAsset != null)
                    {
                        int refCount = GetReferenceCount(item.relativePath);
                        DefaultGUI.Label(cellRect, refCount.ToString(), args.selected, args.focused);
                    }
                    break;
            }
        }

        protected override void SelectionChanged(IList<int> selectedIds)
        {
            base.SelectionChanged(selectedIds);
            
            var selectedCues = new List<PureAudioCue>();
            foreach (int id in selectedIds)
            {
                var item = FindItem(id, rootItem) as PureAudioTreeViewItem;
                if (item != null && !item.isDirectory && item.cueAsset != null)
                {
                    selectedCues.Add(item.cueAsset);
                }
            }

            OnSelectionChanged?.Invoke(selectedCues);
        }

        private void EnsureDirectoryExists()
        {
            if (!Directory.Exists(_basePath))
            {
                Directory.CreateDirectory(_basePath);
                AssetDatabase.Refresh();
            }
        }

        // 物理ファイルやフォルダ操作のためのヘルパーメソッド
        public string GetSelectedPath()
        {
            var selected = GetSelection();
            if (selected.Count == 0) return _basePath;

            var item = FindItem(selected[0], rootItem) as PureAudioTreeViewItem;
            if (item != null)
            {
                if (item.isDirectory) return item.relativePath;
                return Path.GetDirectoryName(item.relativePath).Replace('\\', '/');
            }
            return _basePath;
        }

        public PureAudioTreeViewItem GetItem(int id)
        {
            return FindItem(id, rootItem) as PureAudioTreeViewItem;
        }

        protected override bool CanStartDrag(CanStartDragArgs args)
        {
            return true;
        }

        protected override void SetupDragAndDrop(SetupDragAndDropArgs args)
        {
            DragAndDrop.PrepareStartDrag();
            var draggedRows = args.draggedItemIDs;
            DragAndDrop.paths = draggedRows.Select(id => GetItem(id)?.relativePath).Where(p => p != null).ToArray();
            DragAndDrop.SetGenericData("PureAudioTreeViewDrag", draggedRows);
            DragAndDrop.StartDrag("PureAudioTreeViewDrag");
        }

        protected override DragAndDropVisualMode HandleDragAndDrop(DragAndDropArgs args)
        {
            var draggedIds = DragAndDrop.GetGenericData("PureAudioTreeViewDrag") as IList<int>;
            if (draggedIds == null || draggedIds.Count == 0)
            {
                return DragAndDropVisualMode.None;
            }

            var parentItem = args.parentItem as PureAudioTreeViewItem;
            string targetFolder = _basePath; // デフォルトはルート
            if (parentItem != null)
            {
                if (parentItem.isDirectory)
                {
                    targetFolder = parentItem.relativePath;
                }
                else
                {
                    targetFolder = Path.GetDirectoryName(parentItem.relativePath).Replace('\\', '/');
                }
            }

            if (args.performDrop)
            {
                AssetDatabase.StartAssetEditing();
                try
                {
                    foreach (int id in draggedIds)
                    {
                        var item = GetItem(id);
                        if (item == null) continue;

                        string sourcePath = item.relativePath;
                        string destPath = Path.Combine(targetFolder, Path.GetFileName(sourcePath)).Replace('\\', '/');

                        if (sourcePath == destPath) continue;

                        string error = AssetDatabase.MoveAsset(sourcePath, destPath);
                        if (!string.IsNullOrEmpty(error))
                        {
                            Debug.LogError($"[PureAudio TreeView] Move failed from '{sourcePath}' to '{destPath}': {error}");
                        }
                    }
                }
                finally
                {
                    AssetDatabase.StopAssetEditing();
                    AssetDatabase.Refresh();
                }

                Reload();
            }

            return DragAndDropVisualMode.Move;
        }
    }
}
