using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

namespace PureAudio.Editor
{
    public class PureAudioEditorWindow : EditorWindow
    {
        [MenuItem("Window/PureAudio/Authoring Editor")]
        public static void ShowWindow()
        {
            var window = GetWindow<PureAudioEditorWindow>("PureAudio Authoring Editor");
            window.minSize = new Vector2(850, 500);
        }

        [SerializeField] private TreeViewState _treeViewState;
        [SerializeField] private MultiColumnHeaderState _multiColumnHeaderState;

        private PureAudioCueTreeView _treeView;
        private string _searchString = "";
        private List<PureAudioCue> _selectedCues = new List<PureAudioCue>();

        // 分割レイアウト用
        private float _splitRatio = 0.4f;
        private Rect _splitRect;
        private bool _isDraggingSplitter = false;

        private Vector2 _rightScrollPos;

        private void OnEnable()
        {
            InitializeTreeView();
        }

        private void InitializeTreeView()
        {
            if (_treeViewState == null) _treeViewState = new TreeViewState();

            // カラムの定義
            var columns = new[]
            {
                new MultiColumnHeaderState.Column
                {
                    headerContent = new GUIContent("Name", "Cue Name / Folder"),
                    headerTextAlignment = TextAlignment.Left,
                    sortedAscending = true,
                    sortingArrowAlignment = TextAlignment.Right,
                    width = 180,
                    minWidth = 100,
                    autoResize = true,
                    allowToggleVisibility = false
                },
                new MultiColumnHeaderState.Column
                {
                    headerContent = new GUIContent("Category", "Sound Category"),
                    headerTextAlignment = TextAlignment.Left,
                    width = 80,
                    minWidth = 40,
                    autoResize = true
                },
                new MultiColumnHeaderState.Column
                {
                    headerContent = new GUIContent("Type", "Playback type"),
                    headerTextAlignment = TextAlignment.Left,
                    width = 80,
                    minWidth = 40,
                    autoResize = true
                },
                new MultiColumnHeaderState.Column
                {
                    headerContent = new GUIContent("Volume", "Base volume"),
                    headerTextAlignment = TextAlignment.Left,
                    width = 50,
                    minWidth = 30,
                    autoResize = true
                },
                new MultiColumnHeaderState.Column
                {
                    headerContent = new GUIContent("Pitch", "Base pitch"),
                    headerTextAlignment = TextAlignment.Left,
                    width = 50,
                    minWidth = 30,
                    autoResize = true
                },
                new MultiColumnHeaderState.Column
                {
                    headerContent = new GUIContent("Max Voices", "Max simultaneous voices"),
                    headerTextAlignment = TextAlignment.Left,
                    width = 70,
                    minWidth = 40,
                    autoResize = true
                },
                new MultiColumnHeaderState.Column
                {
                    headerContent = new GUIContent("Spatial", "Spatial blend (2D=0, 3D=1)"),
                    headerTextAlignment = TextAlignment.Left,
                    width = 50,
                    minWidth = 30,
                    autoResize = true
                },
                new MultiColumnHeaderState.Column
                {
                    headerContent = new GUIContent("Loop", "Is looping enabled"),
                    headerTextAlignment = TextAlignment.Left,
                    width = 40,
                    minWidth = 30,
                    autoResize = true
                },
                new MultiColumnHeaderState.Column
                {
                    headerContent = new GUIContent("Addressable", "Is managed by Addressables"),
                    headerTextAlignment = TextAlignment.Left,
                    width = 80,
                    minWidth = 50,
                    autoResize = true
                },
                new MultiColumnHeaderState.Column
                {
                    headerContent = new GUIContent("References", "Number of references in the project"),
                    headerTextAlignment = TextAlignment.Left,
                    width = 70,
                    minWidth = 40,
                    autoResize = true
                }
            };

            var headerState = new MultiColumnHeaderState(columns);
            try
            {
                if (_multiColumnHeaderState != null && 
                    _multiColumnHeaderState.columns != null && 
                    _multiColumnHeaderState.columns.Length == columns.Length)
                {
                    if (MultiColumnHeaderState.CanOverwriteSerializedFields(_multiColumnHeaderState, headerState))
                    {
                        MultiColumnHeaderState.OverwriteSerializedFields(_multiColumnHeaderState, headerState);
                    }
                    else
                    {
                        _multiColumnHeaderState = headerState;
                    }
                }
                else
                {
                    _multiColumnHeaderState = headerState;
                }
            }
            catch (System.Exception)
            {
                _multiColumnHeaderState = headerState;
            }

            var header = new MultiColumnHeader(headerState);
            _treeView = new PureAudioCueTreeView(_treeViewState, header);
            _treeView.OnSelectionChanged += OnSelectionChanged;
        }

        private void OnSelectionChanged(List<PureAudioCue> selectedCues)
        {
            _selectedCues = selectedCues;
            Repaint();
        }

        private void OnGUI()
        {
            HandleKeyboardShortcuts();
            DrawToolbar();

            // 分割位置の設定
            float leftWidth = position.width * _splitRatio;
            float rightWidth = position.width * (1.0f - _splitRatio) - 4f;

            using (new EditorGUILayout.HorizontalScope())
            {
                // 左ペイン
                using (new EditorGUILayout.VerticalScope(GUILayout.Width(leftWidth)))
                {
                    DrawLeftPane(leftWidth);
                }

                // スプリッター
                DrawSplitter();

                // 右ペイン
                using (new EditorGUILayout.VerticalScope(GUILayout.Width(rightWidth)))
                {
                    DrawRightPane(rightWidth);
                }
            }

            HandleSplitterDrag();
        }

        private void DrawToolbar()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                if (GUILayout.Button("Import CSV", EditorStyles.toolbarButton, GUILayout.Width(80)))
                {
                    PureAudioExcelSync.ImportCsvToAssets();
                    _treeView.Reload();
                }
                if (GUILayout.Button("Export CSV", EditorStyles.toolbarButton, GUILayout.Width(80)))
                {
                    PureAudioExcelSync.ExportAssetsToCsv();
                }
                if (GUILayout.Button("Bulk Replace", EditorStyles.toolbarButton, GUILayout.Width(90)))
                {
                    PureAudioBulkReplaceWindow.ShowWindow(_selectedCues);
                }

                GUILayout.Space(20);
                GUILayout.Label("Authoring Editor", EditorStyles.miniBoldLabel);
                
                GUILayout.FlexibleSpace();

                // 検索窓の配置
                var searchRect = GUILayoutUtility.GetRect(150, 16, EditorStyles.toolbarSearchField, GUILayout.Width(200));
                string newSearch = CustomSearchField(searchRect, _searchString);
                if (newSearch != _searchString)
                {
                    _searchString = newSearch;
                    _treeView.searchString = _searchString;
                }
            }
        }

        private string CustomSearchField(Rect rect, string text)
        {
            // Unityの標準検索窓を利用
            return EditorGUI.TextField(rect, text, EditorStyles.toolbarSearchField);
        }

        private void DrawLeftPane(float width)
        {
            // 操作ボタンを上部にサブツールバーとして配置し、高さ見切れを確実に解消する
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                if (GUILayout.Button("Add Cue", EditorStyles.toolbarButton, GUILayout.Width(65)))
                {
                    CreateNewCue();
                }
                if (GUILayout.Button("Add Folder", EditorStyles.toolbarButton, GUILayout.Width(75)))
                {
                    CreateNewFolder();
                }
                if (GUILayout.Button("Delete", EditorStyles.toolbarButton, GUILayout.Width(55)))
                {
                    DeleteSelected();
                }
                GUILayout.FlexibleSpace();
            }

            // TreeView描画領域 (残りの高さをすべて使用)
            Rect treeRect = EditorGUILayout.GetControlRect(false, GUILayout.ExpandHeight(true));
            _treeView.OnGUI(treeRect);
        }

        private void DrawSplitter()
        {
            _splitRect = GUILayoutUtility.GetRect(4f, position.height, GUILayout.ExpandHeight(true), GUILayout.Width(4f));
            EditorGUIUtility.AddCursorRect(_splitRect, MouseCursor.ResizeHorizontal);
            
            // スプリッターのライン描画
            Color splitterColor = EditorGUIUtility.isProSkin ? new Color(0.15f, 0.15f, 0.15f) : new Color(0.6f, 0.6f, 0.6f);
            EditorGUI.DrawRect(_splitRect, splitterColor);
        }

        private void HandleSplitterDrag()
        {
            if (Event.current.type == EventType.MouseDown && _splitRect.Contains(Event.current.mousePosition))
            {
                _isDraggingSplitter = true;
            }

            if (_isDraggingSplitter)
            {
                float newRatio = Event.current.mousePosition.x / position.width;
                float minRatio = 250f / position.width;
                _splitRatio = Mathf.Clamp(newRatio, minRatio, 0.7f);
                Repaint();

                if (Event.current.type == EventType.MouseUp)
                {
                    _isDraggingSplitter = false;
                }
            }
        }

        private void DrawRightPane(float width)
        {
            if (_selectedCues == null || _selectedCues.Count == 0)
            {
                using (new EditorGUILayout.VerticalScope("box", GUILayout.ExpandHeight(true)))
                {
                    EditorGUILayout.Space(20);
                    EditorGUILayout.LabelField("アセットが選択されていません", EditorStyles.boldLabel);
                    EditorGUILayout.HelpBox("左側のツリービューからCueアセットを選択すると、ここにパラメータが表示されます。", MessageType.Info);
                }
                return;
            }

            using (var scroll = new EditorGUILayout.ScrollViewScope(_rightScrollPos))
            {
                _rightScrollPos = scroll.scrollPosition;

                bool isMultiSelect = _selectedCues.Count > 1;

                if (isMultiSelect)
                {
                    EditorGUILayout.HelpBox($"{_selectedCues.Count} 個のCueアセットを選択中 (一括編集モード)", MessageType.Info);
                }

                // 共通ヘッダー情報
                using (new EditorGUILayout.VerticalScope("box"))
                {
                    EditorGUILayout.LabelField("Cue Basic Info", EditorStyles.boldLabel);
                    EditorGUILayout.Space(5);

                    // Cue Name (マルチセレクト時は編集不可)
                    if (!isMultiSelect)
                    {
                        var firstCue = _selectedCues[0];
                        string prevName = firstCue.cueName;
                        string newName = EditorGUILayout.DelayedTextField("Cue Name", prevName);
                        if (newName != prevName && !string.IsNullOrEmpty(newName))
                        {
                            RenameCueAsset(firstCue, newName);
                        }
                    }
                    else
                    {
                        EditorGUILayout.LabelField("Cue Name", "[複数選択時は変更不可]", EditorStyles.miniLabel);
                    }

                    // Category
                    DrawCategorySelector();
 
                    // Playback Type
                    DrawPlaybackTypeSelector();

                    // References
                    if (!isMultiSelect)
                    {
                        var firstCue = _selectedCues[0];
                        string assetPath = AssetDatabase.GetAssetPath(firstCue);
                        
                        int inspectorCount, scriptCount;
                        List<string> refPaths;
                        _treeView.GetReferenceDetails(assetPath, out inspectorCount, out scriptCount, out refPaths);
                        int totalCount = inspectorCount + scriptCount;

                        EditorGUILayout.LabelField("References (被参照数)", $"{totalCount} (Inspector: {inspectorCount}, Scripts: {scriptCount})");

                        if (refPaths.Count > 0)
                        {
                            EditorGUILayout.Space(2);
                            EditorGUILayout.LabelField("Reference Sources (参照元一覧)", EditorStyles.miniBoldLabel);
                            using (new EditorGUILayout.VerticalScope("box"))
                            {
                                foreach (var path in refPaths)
                                {
                                    var obj = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
                                    if (obj != null)
                                    {
                                        using (new EditorGUILayout.HorizontalScope())
                                        {
                                            using (new EditorGUI.DisabledGroupScope(true))
                                            {
                                                EditorGUILayout.ObjectField(GUIContent.none, obj, typeof(UnityEngine.Object), false);
                                            }
                                        }
                                    }
                                    else
                                    {
                                        EditorGUILayout.LabelField(Path.GetFileName(path), path, EditorStyles.miniLabel);
                                    }
                                }
                            }
                        }
                    }

                    EditorGUILayout.Space(5);
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (GUILayout.Button("Play Preview (Space)", GUILayout.Height(25)))
                        {
                            PlaySelectedCuePreview();
                        }
                        if (GUILayout.Button("Stop Preview", GUILayout.Height(25)))
                        {
                            StopPreview();
                        }
                    }
                }

                EditorGUILayout.Space(10);

                // 音量 & ピッチ (ランダム対応)
                using (new EditorGUILayout.VerticalScope("box"))
                {
                    EditorGUILayout.LabelField("Volume & Pitch Parameters", EditorStyles.boldLabel);
                    EditorGUILayout.Space(5);

                    DrawSliderParameter(new GUIContent("Volume", "ベース音量。各Cueに設定する基本音量（0.0〜1.0）です。"), cue => cue.volume, (cue, val) => cue.volume = val, 0f, 1f);
                    DrawSliderParameter(new GUIContent("Volume Random Range", "音量のランダム化幅。設定した値の範囲でトリガーごとに音量がランダムに増減します。"), cue => cue.volumeRandomRange, (cue, val) => cue.volumeRandomRange = val, 0f, 0.5f);
                    DrawSliderParameter(new GUIContent("Pitch", "基本ピッチ倍率（再生速度と高低）。0.5で半分の速度、2.0で2倍速になります。"), cue => cue.pitch, (cue, val) => cue.pitch = val, 0.5f, 2.0f);
                    DrawSliderParameter(new GUIContent("Pitch Random Range", "ピッチのランダム化幅。トリガーごとにピッチがランダムに変化し、自然な音のバリエーションを作ります。"), cue => cue.pitchRandomRange, (cue, val) => cue.pitchRandomRange = val, 0f, 0.5f);
                }

                EditorGUILayout.Space(10);

                // 発音制限・優先度 (Advanced設定)
                using (new EditorGUILayout.VerticalScope("box"))
                {
                    EditorGUILayout.LabelField("Voice Limit & Priority (Advanced Settings)", EditorStyles.boldLabel);
                    EditorGUILayout.Space(5);

                    DrawIntParameter(new GUIContent("Max Simultaneous Voices (0=Unlimited)", "このCueの最大同時発音数。制限に達した場合は Stolen Behavior の設定に従って制御されます。0で無制限。"), cue => cue.maxVoices, (cue, val) => cue.maxVoices = Mathf.Max(0, val));
                    DrawLimitBehaviorSelector();
                    DrawIntParameter(new GUIContent("Priority (0 - 255)", "発音の優先度 (0〜255)。発音制限時に優先度が低いボイスが優先的にミュート・停止されます。"), cue => cue.priority, (cue, val) => cue.priority = Mathf.Clamp(val, 0, 255));
                }

                EditorGUILayout.Space(10);

                // ボリュームADSRエンベロープ設定
                using (new EditorGUILayout.VerticalScope("box"))
                {
                    EditorGUILayout.LabelField("ADSR Volume Envelope (Advanced ADSR)", EditorStyles.boldLabel);
                    EditorGUILayout.Space(5);

                    DrawSliderParameter(new GUIContent("Delay (sec)", "再生開始トリガーから実際に音が出始めるまでの遅延時間（秒）。"), cue => cue.delay, (cue, val) => cue.delay = Mathf.Max(0f, val), 0f, 5f);
                    DrawSliderParameter(new GUIContent("Attack Time (sec)", "ADSRエンベロープのアタック時間（秒）。音がゼロから最大音量に達するまでのフェードイン時間です。"), cue => cue.attackTime, (cue, val) => cue.attackTime = Mathf.Max(0f, val), 0f, 5f);
                    DrawSliderParameter(new GUIContent("Decay Time (sec)", "ADSRエンベロープのディケイ時間（秒）。最大音量からサスティンレベルまで減衰する時間です。"), cue => cue.decayTime, (cue, val) => cue.decayTime = Mathf.Max(0f, val), 0f, 5f);
                    DrawSliderParameter(new GUIContent("Sustain Level", "ADSRエンベロープのサスティンレベル（音量比率）。キーオン（再生）中に維持される音量比率です。"), cue => cue.sustainLevel, (cue, val) => cue.sustainLevel = Mathf.Clamp01(val), 0f, 1f);
                    DrawSliderParameter(new GUIContent("Release Time (sec)", "ADSRエンベロープのリリース時間（秒）。再生停止指示（Stop）を受けてから完全に無音になるまでのフェードアウト時間です。"), cue => cue.releaseTime, (cue, val) => cue.releaseTime = Mathf.Max(0f, val), 0f, 5f);
                }

                EditorGUILayout.Space(10);

                // 3D空間・減衰
                using (new EditorGUILayout.VerticalScope("box"))
                {
                    EditorGUILayout.LabelField("3D Spatial & Distance Settings", EditorStyles.boldLabel);
                    EditorGUILayout.Space(5);

                    DrawSliderParameter(new GUIContent("Spatial Blend (0=2D, 1=3D)", "3D空間のブレンド率。0.0で完全な2D（左右均等ステレオ）、1.0で3D（定位・距離減衰あり）になります。"), cue => cue.spatialBlend, (cue, val) => cue.spatialBlend = val, 0f, 1f);
                    DrawSliderParameter(new GUIContent("Min Distance (Roll-off Start)", "3D減衰の最小距離。この距離までは音量が減衰せず最大値を維持します。"), cue => cue.minDistance, (cue, val) => cue.minDistance = val, 0.1f, 100f);
                    DrawSliderParameter(new GUIContent("Max Distance (Roll-off End)", "3D減衰の最大距離。これ以上離れると音量が最小（無音など）になります。"), cue => cue.maxDistance, (cue, val) => cue.maxDistance = Mathf.Max(cue.minDistance + 0.1f, val), 0.1f, 500f);
                }

                EditorGUILayout.Space(10);

                // ループ設定
                using (new EditorGUILayout.VerticalScope("box"))
                {
                    EditorGUILayout.LabelField("Looping Settings", EditorStyles.boldLabel);
                    EditorGUILayout.Space(5);

                    DrawToggleParameter(new GUIContent("Loop Enabled", "ループ再生を有効にするかどうか。"), cue => cue.isLooping, (cue, val) => cue.isLooping = val);
                    
                    // ループタイム指定
                    DrawSliderParameter(new GUIContent("Loop Begin Time (sec)", "イントロ付きループ用のループ開始位置（秒）。イントロが終わってループに入るタイムを指定します。"), cue => cue.loopBeginTime, (cue, val) => cue.loopBeginTime = Mathf.Max(0f, val), 0f, 60f);
                    DrawSliderParameter(new GUIContent("Loop End Time (sec)", "イントロ付きループ用のループ終了位置（秒）。このタイムに達すると自動的に開始位置（LoopBegin）にシークします。"), cue => cue.loopEndTime, (cue, val) => cue.loopEndTime = Mathf.Max(0f, val), 0f, 300f);
                }

                EditorGUILayout.Space(10);

                // Addressables設定
                using (new EditorGUILayout.VerticalScope("box"))
                {
                    EditorGUILayout.LabelField("Addressable Assets Settings", EditorStyles.boldLabel);
                    EditorGUILayout.Space(5);

                    DrawToggleParameter(new GUIContent("Is Addressable", "このCueアセットがAddressablesで管理され、非同期ロードされるかどうか。"), cue => cue.isAddressable, (cue, val) => cue.isAddressable = val);
                }

                EditorGUILayout.Space(10);

                // AudioClip 登録 & ドラッグ＆ドロップエリア
                DrawAudioClipSection();
            }
        }

        #region 各種パラメータ編集用ヘルパー

        private void SaveAndSyncCue(PureAudioCue cue)
        {
            EditorUtility.SetDirty(cue);
            if (EditorApplication.isPlaying && PureAudioEngine.Instance != null)
            {
                PureAudioEngine.Instance.SyncPlayModeCue(cue);
            }
        }

        private void DrawSliderParameter(GUIContent label, Func<PureAudioCue, float> getter, Action<PureAudioCue, float> setter, float min, float max)
        {
            float firstVal = getter(_selectedCues[0]);
            bool mixed = false;

            for (int i = 1; i < _selectedCues.Count; i++)
            {
                if (!Mathf.Approximately(getter(_selectedCues[i]), firstVal))
                {
                    mixed = true;
                    break;
                }
            }

            EditorGUI.showMixedValue = mixed;
            float newVal = EditorGUILayout.Slider(label, firstVal, min, max);
            EditorGUI.showMixedValue = false;

            if (!mixed && !Mathf.Approximately(newVal, firstVal) || (mixed && GUI.changed))
            {
                foreach (var cue in _selectedCues)
                {
                    setter(cue, newVal);
                    SaveAndSyncCue(cue);
                }
            }
        }

        private void DrawIntParameter(GUIContent label, Func<PureAudioCue, int> getter, Action<PureAudioCue, int> setter)
        {
            int firstVal = getter(_selectedCues[0]);
            bool mixed = false;

            for (int i = 1; i < _selectedCues.Count; i++)
            {
                if (getter(_selectedCues[i]) != firstVal)
                {
                    mixed = true;
                    break;
                }
            }

            EditorGUI.showMixedValue = mixed;
            int newVal = EditorGUILayout.IntField(label, firstVal);
            EditorGUI.showMixedValue = false;

            if (!mixed && newVal != firstVal || (mixed && GUI.changed))
            {
                foreach (var cue in _selectedCues)
                {
                    setter(cue, newVal);
                    SaveAndSyncCue(cue);
                }
            }
        }

        private void DrawToggleParameter(GUIContent label, Func<PureAudioCue, bool> getter, Action<PureAudioCue, bool> setter)
        {
            bool firstVal = getter(_selectedCues[0]);
            bool mixed = false;

            for (int i = 1; i < _selectedCues.Count; i++)
            {
                if (getter(_selectedCues[i]) != firstVal)
                {
                    mixed = true;
                    break;
                }
            }

            EditorGUI.showMixedValue = mixed;
            bool newVal = EditorGUILayout.Toggle(label, firstVal);
            EditorGUI.showMixedValue = false;

            if (!mixed && newVal != firstVal || (mixed && GUI.changed))
            {
                foreach (var cue in _selectedCues)
                {
                    setter(cue, newVal);
                    SaveAndSyncCue(cue);
                }
            }
        }

        private void DrawCategorySelector()
        {
            // 利用可能な全カテゴリを取得
            string[] catGuids = AssetDatabase.FindAssets("t:PureAudioCategory");
            var categories = new List<PureAudioCategory>();
            var options = new List<string> { "None" };

            foreach (var guid in catGuids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var cat = AssetDatabase.LoadAssetAtPath<PureAudioCategory>(path);
                if (cat != null)
                {
                    categories.Add(cat);
                    options.Add(cat.categoryName);
                }
            }

            PureAudioCategory firstCat = _selectedCues[0].category;
            int selectedIndex = 0;
            if (firstCat != null)
            {
                selectedIndex = options.IndexOf(firstCat.categoryName);
                if (selectedIndex < 0) selectedIndex = 0;
            }

            bool mixed = false;
            for (int i = 1; i < _selectedCues.Count; i++)
            {
                if (_selectedCues[i].category != firstCat)
                {
                    mixed = true;
                    break;
                }
            }

            EditorGUI.showMixedValue = mixed;
            int newIndex = EditorGUILayout.Popup("Category", selectedIndex, options.ToArray());
            EditorGUI.showMixedValue = false;

            if ((!mixed && newIndex != selectedIndex) || (mixed && GUI.changed))
            {
                PureAudioCategory targetCat = newIndex > 0 ? categories[newIndex - 1] : null;
                foreach (var cue in _selectedCues)
                {
                    cue.category = targetCat;
                    SaveAndSyncCue(cue);
                }
            }
        }

        private void DrawPlaybackTypeSelector()
        {
            PlayType firstVal = _selectedCues[0].playbackType;
            bool mixed = false;

            for (int i = 1; i < _selectedCues.Count; i++)
            {
                if (_selectedCues[i].playbackType != firstVal)
                {
                    mixed = true;
                    break;
                }
            }

            EditorGUI.showMixedValue = mixed;
            PlayType newVal = (PlayType)EditorGUILayout.EnumPopup("Playback Type", firstVal);
            EditorGUI.showMixedValue = false;

            if (!mixed && newVal != firstVal || (mixed && GUI.changed))
            {
                foreach (var cue in _selectedCues)
                {
                    cue.playbackType = newVal;
                    SaveAndSyncCue(cue);
                }
            }

            // Playback Type が Random の時のみ NoRepeatHistoryCount を描画する
            if (!mixed && newVal == PlayType.Random)
            {
                int maxHistory = 0;
                if (_selectedCues.Count == 1)
                {
                    var cue = _selectedCues[0];
                    int clipCount = (cue.directAudioClips != null ? cue.directAudioClips.Count : 0) + 
                                     (cue.audioClipReferences != null ? cue.audioClipReferences.Count : 0);
                    maxHistory = Mathf.Max(0, clipCount - 1);
                }
                else
                {
                    maxHistory = 10;
                }

                DrawSliderParameter(
                    new GUIContent("No-Repeat History Count", "ランダム再生時に、一度再生した音を次回以降の抽選から除外する過去の再生数。0で完全ランダム。最大は (Clip数 - 1) までです。"), 
                    cue => (float)cue.noRepeatHistoryCount, 
                    (cue, val) => cue.noRepeatHistoryCount = Mathf.Clamp((int)val, 0, maxHistory), 
                    0f, 
                    maxHistory
                );
            }
        }

        private void DrawLimitBehaviorSelector()
        {
            LimitBehavior firstVal = _selectedCues[0].limitBehavior;
            bool mixed = false;

            for (int i = 1; i < _selectedCues.Count; i++)
            {
                if (_selectedCues[i].limitBehavior != firstVal)
                {
                    mixed = true;
                    break;
                }
            }

            EditorGUI.showMixedValue = mixed;
            LimitBehavior newVal = (LimitBehavior)EditorGUILayout.EnumPopup("Voice Stolen Behavior", firstVal);
            EditorGUI.showMixedValue = false;

            if (!mixed && newVal != firstVal || (mixed && GUI.changed))
            {
                foreach (var cue in _selectedCues)
                {
                    cue.limitBehavior = newVal;
                    SaveAndSyncCue(cue);
                }
            }
        }

        private void DrawAudioClipSection()
        {
            using (new EditorGUILayout.VerticalScope("box"))
            {
                EditorGUILayout.LabelField("Registered Audio Clips (Direct)", EditorStyles.boldLabel);
                EditorGUILayout.Space(5);

                // 単一選択時のみ、リスト表示・削除ができる
                if (_selectedCues.Count == 1)
                {
                    var cue = _selectedCues[0];
                    if (cue.directAudioClips == null) cue.directAudioClips = new List<AudioClip>();

                    if (cue.directAudioClips.Count == 0)
                    {
                        EditorGUILayout.HelpBox("オーディオファイルが紐づいていません。下のエリアにドラッグ＆ドロップしてください。", MessageType.Warning);
                    }

                    for (int i = 0; i < cue.directAudioClips.Count; i++)
                    {
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            var clip = EditorGUILayout.ObjectField($"Clip {i + 1}", cue.directAudioClips[i], typeof(AudioClip), false) as AudioClip;
                            if (clip != cue.directAudioClips[i])
                            {
                                cue.directAudioClips[i] = clip;
                                EditorUtility.SetDirty(cue);
                            }

                            if (GUILayout.Button("Delete", GUILayout.Width(60)))
                            {
                                cue.directAudioClips.RemoveAt(i);
                                EditorUtility.SetDirty(cue);
                                break;
                            }
                        }
                    }
                }
                else
                {
                    EditorGUILayout.LabelField("Direct Clips", "[複数選択時は各個別アセットから編集してください]", EditorStyles.miniLabel);
                }

                // ドラッグ＆ドロップエリアの描画
                Event evt = Event.current;
                Rect dropArea = GUILayoutUtility.GetRect(0.0f, 60.0f, GUILayout.ExpandWidth(true));
                GUI.Box(dropArea, "AudioClip のドラッグ＆ドロップエリア\n(ここに音声ファイルをドロップして割り当て)", "TextField");

                switch (evt.type)
                {
                    case EventType.DragUpdated:
                    case EventType.DragPerform:
                        if (!dropArea.Contains(evt.mousePosition))
                            break;

                        DragAndDrop.visualMode = DragAndDropVisualMode.Copy;

                        if (evt.type == EventType.DragPerform)
                        {
                            DragAndDrop.AcceptDrag();

                            foreach (UnityEngine.Object draggedObject in DragAndDrop.objectReferences)
                            {
                                if (draggedObject is AudioClip clip)
                                {
                                    foreach (var cue in _selectedCues)
                                    {
                                        if (cue.directAudioClips == null) cue.directAudioClips = new List<AudioClip>();
                                        if (!cue.directAudioClips.Contains(clip))
                                        {
                                            cue.directAudioClips.Add(clip);
                                            EditorUtility.SetDirty(cue);
                                        }
                                    }
                                }
                            }
                            evt.Use();
                        }
                        break;
                }
            }
        }

        #endregion

        #region アセット作成・削除ロジック

        private void CreateNewCue()
        {
            string targetFolder = _treeView.GetSelectedPath();
            string fullPath = Path.Combine(Directory.GetCurrentDirectory(), targetFolder);
            if (!Directory.Exists(fullPath)) Directory.CreateDirectory(fullPath);

            string defaultName = "NewCue";
            string assetPath = $"{targetFolder}/{defaultName}.asset";
            
            // 重複回避名
            assetPath = AssetDatabase.GenerateUniqueAssetPath(assetPath);

            var cue = ScriptableObject.CreateInstance<PureAudioCue>();
            cue.cueName = Path.GetFileNameWithoutExtension(assetPath);
            cue.volume = 1f;
            cue.pitch = 1f;
            cue.spatialBlend = 0f; // デフォルト2D
            
            AssetDatabase.CreateAsset(cue, assetPath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            _treeView.Reload();

            // 新規作成したものを選択状態にする
            var item = AssetDatabase.LoadAssetAtPath<PureAudioCue>(assetPath);
            if (item != null)
            {
                int id = _treeView.GetOrCreateIdForPath(assetPath);
                _treeView.SetSelection(new[] { id }, TreeViewSelectionOptions.RevealAndFrame);
            }
        }

        private void CreateNewFolder()
        {
            string parentFolder = _treeView.GetSelectedPath();
            string newFolderPath = Path.Combine(parentFolder, "NewFolder").Replace('\\', '/');
            string uniquePath = AssetDatabase.GenerateUniqueAssetPath(newFolderPath);

            Directory.CreateDirectory(Path.Combine(Directory.GetCurrentDirectory(), uniquePath));
            AssetDatabase.Refresh();

            _treeView.Reload();

            int id = _treeView.GetOrCreateIdForPath(uniquePath);
            _treeView.SetSelection(new[] { id }, TreeViewSelectionOptions.RevealAndFrame);
        }

        private void DeleteSelected()
        {
            var selectedIds = _treeView.GetSelection();
            if (selectedIds.Count == 0) return;

            if (!EditorUtility.DisplayDialog("アセットの削除確認", "選択されたアセット/フォルダを削除してもよろしいですか？\nこの操作は元に戻せません。", "削除する", "キャンセル"))
            {
                return;
            }

            // TreeViewItemからパスを特定してアセット削除
            foreach (var id in selectedIds)
            {
                // find item
                var treeItem = _treeView.GetItem(id);
                if (treeItem != null)
                {
                    if (treeItem.isDirectory)
                    {
                        if (Directory.Exists(treeItem.relativePath))
                        {
                            Directory.Delete(treeItem.relativePath, true);
                            File.Delete(treeItem.relativePath + ".meta");
                        }
                    }
                    else
                    {
                        AssetDatabase.DeleteAsset(treeItem.relativePath);
                    }
                }
            }

            AssetDatabase.Refresh();
            _treeView.Reload();
            _treeView.SetSelection(new int[0]);
        }

        private void RenameCueAsset(PureAudioCue cue, string newName)
        {
            string oldPath = AssetDatabase.GetAssetPath(cue);
            string directory = Path.GetDirectoryName(oldPath);
            string newPath = Path.Combine(directory, newName + ".asset").Replace('\\', '/');

            // リネームエラー防止
            if (File.Exists(Path.Combine(Directory.GetCurrentDirectory(), newPath)))
            {
                Debug.LogWarning($"[PureAudio] Rename failed. Asset already exists at path: {newPath}");
                return;
            }

            string error = AssetDatabase.RenameAsset(oldPath, newName);
            if (string.IsNullOrEmpty(error))
            {
                cue.cueName = newName;
                EditorUtility.SetDirty(cue);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                
                _treeView.Reload();

                int id = _treeView.GetOrCreateIdForPath(newPath);
                _treeView.SetSelection(new[] { id }, TreeViewSelectionOptions.RevealAndFrame);
            }
            else
            {
                Debug.LogError($"[PureAudio] Rename Error: {error}");
            }
        }

        private void HandleKeyboardShortcuts()
        {
            Event evt = Event.current;
            if (evt.type == EventType.KeyDown && evt.keyCode == KeyCode.Space)
            {
                if (_selectedCues != null && _selectedCues.Count == 1)
                {
                    if (IsPreviewPlaying())
                    {
                        StopPreview();
                    }
                    else
                    {
                        PlaySelectedCuePreview();
                    }
                    evt.Use();
                }
            }
        }

        private void PlaySelectedCuePreview()
        {
            if (_selectedCues == null || _selectedCues.Count != 1) return;
            var cue = _selectedCues[0];
            if (cue.directAudioClips != null && cue.directAudioClips.Count > 0)
            {
                StopPreview();
                int idx = cue.GetNextClipIndex(cue.directAudioClips.Count);
                if (idx >= 0 && idx < cue.directAudioClips.Count)
                {
                    PlayPreview(cue.directAudioClips[idx]);
                    Debug.Log($"[PureAudio Editor] Preview playing: {cue.directAudioClips[idx].name} (Cue: {cue.cueName})");
                }
            }
            else
            {
                Debug.LogWarning($"[PureAudio Editor] No direct AudioClip registered to preview for Cue: {cue.cueName}");
            }
        }

        private static void PlayPreview(AudioClip clip)
        {
            if (clip == null) return;
            try
            {
                var assembly = typeof(AudioImporter).Assembly;
                var type = assembly.GetType("UnityEditor.AudioUtil");
                var method = type.GetMethod("PlayPreviewClip",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public,
                    null,
                    new System.Type[] { typeof(AudioClip), typeof(int), typeof(bool) },
                    null);
                if (method != null)
                {
                    method.Invoke(null, new object[] { clip, 0, false });
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[PureAudio Editor] PlayPreview failed: {ex.Message}");
            }
        }

        private static void StopPreview()
        {
            try
            {
                var assembly = typeof(AudioImporter).Assembly;
                var type = assembly.GetType("UnityEditor.AudioUtil");
                var method = type.GetMethod("StopAllPreviewClips",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
                if (method != null)
                {
                    method.Invoke(null, null);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[PureAudio Editor] StopPreview failed: {ex.Message}");
            }
        }

        private static bool IsPreviewPlaying()
        {
            try
            {
                var assembly = typeof(AudioImporter).Assembly;
                var type = assembly.GetType("UnityEditor.AudioUtil");
                var method = type.GetMethod("IsPreviewClipPlaying",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
                if (method != null)
                {
                    return (bool)method.Invoke(null, null);
                }
            }
            catch
            {
                // ignore
            }
            return false;
        }

        #endregion
    }

    public class PureAudioBulkReplaceWindow : EditorWindow
    {
        private List<PureAudioCue> _targets = new List<PureAudioCue>();
        private string _findString = "";
        private string _replaceString = "";

        // 一括適用パラメータ
        private bool _updateVolume = false;
        private float _bulkVolume = 1f;

        private bool _updatePitch = false;
        private float _bulkPitch = 1f;

        private bool _updateCategory = false;
        private PureAudioCategory _bulkCategory = null;

        private bool _updatePlaybackType = false;
        private PlayType _bulkPlaybackType = PlayType.Single;

        private bool _updateAddressable = false;
        private bool _bulkAddressable = false;

        public static void ShowWindow(List<PureAudioCue> selectedCues)
        {
            var window = GetWindow<PureAudioBulkReplaceWindow>("PureAudio Bulk Replace / Update");
            window.minSize = new Vector2(400, 450);
            window._targets = new List<PureAudioCue>(selectedCues);
            if (window._targets.Count == 0)
            {
                // 選択がない場合は全てのCueを対象にする
                string[] guids = AssetDatabase.FindAssets("t:PureAudioCue");
                foreach (var guid in guids)
                {
                    string path = AssetDatabase.GUIDToAssetPath(guid);
                    var cue = AssetDatabase.LoadAssetAtPath<PureAudioCue>(path);
                    if (cue != null) window._targets.Add(cue);
                }
            }
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField($"Target Cues: {_targets.Count} items", EditorStyles.boldLabel);
            EditorGUILayout.Space(10);

            // 1. 名前置換セクション
            using (new EditorGUILayout.VerticalScope("box"))
            {
                EditorGUILayout.LabelField("Name Replace (アセット名置換)", EditorStyles.boldLabel);
                _findString = EditorGUILayout.TextField("Find (検索文字列)", _findString);
                _replaceString = EditorGUILayout.TextField("Replace (置換文字列)", _replaceString);

                if (GUILayout.Button("Replace Names (名前を置換実行)"))
                {
                    ExecuteNameReplace();
                }
            }

            EditorGUILayout.Space(10);

            // 2. パラメータ一括変更セクション
            using (new EditorGUILayout.VerticalScope("box"))
            {
                EditorGUILayout.LabelField("Bulk Update Parameters (パラメータ一括変換)", EditorStyles.boldLabel);
                EditorGUILayout.Space(5);

                // Volume
                using (new EditorGUILayout.HorizontalScope())
                {
                    _updateVolume = EditorGUILayout.ToggleLeft("Volume", _updateVolume, GUILayout.Width(100));
                    if (_updateVolume) _bulkVolume = EditorGUILayout.Slider(_bulkVolume, 0f, 1f);
                }

                // Pitch
                using (new EditorGUILayout.HorizontalScope())
                {
                    _updatePitch = EditorGUILayout.ToggleLeft("Pitch", _updatePitch, GUILayout.Width(100));
                    if (_updatePitch) _bulkPitch = EditorGUILayout.Slider(_bulkPitch, 0.5f, 2.0f);
                }

                // Category
                using (new EditorGUILayout.HorizontalScope())
                {
                    _updateCategory = EditorGUILayout.ToggleLeft("Category", _updateCategory, GUILayout.Width(100));
                    if (_updateCategory)
                    {
                        _bulkCategory = EditorGUILayout.ObjectField(_bulkCategory, typeof(PureAudioCategory), false) as PureAudioCategory;
                    }
                }

                // PlaybackType
                using (new EditorGUILayout.HorizontalScope())
                {
                    _updatePlaybackType = EditorGUILayout.ToggleLeft("Playback Type", _updatePlaybackType, GUILayout.Width(100));
                    if (_updatePlaybackType) _bulkPlaybackType = (PlayType)EditorGUILayout.EnumPopup(_bulkPlaybackType);
                }

                // Addressable
                using (new EditorGUILayout.HorizontalScope())
                {
                    _updateAddressable = EditorGUILayout.ToggleLeft("Is Addressable", _updateAddressable, GUILayout.Width(100));
                    if (_updateAddressable) _bulkAddressable = EditorGUILayout.Toggle(_bulkAddressable);
                }

                EditorGUILayout.Space(10);

                if (GUILayout.Button("Apply Parameter Updates (パラメータを一括適用)"))
                {
                    ExecuteParameterUpdate();
                }
            }
        }

        private void ExecuteNameReplace()
        {
            if (string.IsNullOrEmpty(_findString))
            {
                EditorUtility.DisplayDialog("Error", "Find string is empty.", "OK");
                return;
            }

            int count = 0;
            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var cue in _targets)
                {
                    if (cue == null) continue;
                    if (cue.cueName.Contains(_findString))
                    {
                        string newName = cue.cueName.Replace(_findString, _replaceString);
                        string oldPath = AssetDatabase.GetAssetPath(cue);
                        string error = AssetDatabase.RenameAsset(oldPath, newName);
                        if (string.IsNullOrEmpty(error))
                        {
                            cue.cueName = newName;
                            EditorUtility.SetDirty(cue);
                            count++;
                        }
                        else
                        {
                            Debug.LogError($"[PureAudio Bulk] Rename error for '{cue.name}': {error}");
                        }
                    }
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
            }

            EditorUtility.DisplayDialog("Success", $"{count} cues renamed successfully.", "OK");
            Close();
        }

        private void ExecuteParameterUpdate()
        {
            int count = 0;
            foreach (var cue in _targets)
            {
                if (cue == null) continue;

                bool changed = false;
                if (_updateVolume) { cue.volume = _bulkVolume; changed = true; }
                if (_updatePitch) { cue.pitch = _bulkPitch; changed = true; }
                if (_updateCategory) { cue.category = _bulkCategory; changed = true; }
                if (_updatePlaybackType) { cue.playbackType = _bulkPlaybackType; changed = true; }
                if (_updateAddressable) { cue.isAddressable = _bulkAddressable; changed = true; }

                if (changed)
                {
                    EditorUtility.SetDirty(cue);
                    count++;
                }
            }

            if (count > 0)
            {
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
            }

            EditorUtility.DisplayDialog("Success", $"{count} cues updated successfully.", "OK");
            Close();
        }
    }
}
