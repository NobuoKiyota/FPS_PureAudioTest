using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace PureAudio.Editor
{
    /// <summary>
    /// PureAudioのボイスプール、親子カテゴリ階層ミキサー、メモリ使用量、CPU負荷などを監視・デバッグするエディタウィンドウ。
    /// </summary>
    public class PureAudioProfilerWindow : EditorWindow
    {
        [MenuItem("Window/PureAudio/Profiler")]
        public static void ShowWindow()
        {
            var window = GetWindow<PureAudioProfilerWindow>("PureAudio Profiler");
            window.minSize = new Vector2(700, 500);
        }
        [SerializeField] private List<PureAudioEngine.PureAudioLog> _cachedLogs = new List<PureAudioEngine.PureAudioLog>();
        [SerializeField] private List<PureAudioEngine.PureAudioLog> _cachedWarnings = new List<PureAudioEngine.PureAudioLog>();

        private Vector2 _voiceScrollPos;
        private Vector2 _logScrollPos;
        private Vector2 _stackScrollPos;
        [SerializeField] private int _selectedLogIndex = -1;
        [SerializeField] private int _selectedWarningIndex = -1;
        private Vector2 _warningScrollPos;
        private Vector2 _warningStackScrollPos;
        private bool _autoScrollWarnings = true;
        [SerializeField] private bool _includeCallStackInExport = false;
        [SerializeField] private int _activeTab = 0; // 0: Live Logs, 1: Warnings, 2: Settings

        // ラウドネス計測用バッファと表示設定
        private readonly float[] _sampleBuffer = new float[256];
        [SerializeField] private bool _showLoudnessMeter = true;
        [SerializeField] private bool _showCategoryLoudness = true;
        private double _lastRepaintTime;
        private bool _autoScrollLogs = true;

        private void OnEnable()
        {
            EditorApplication.update += OnEditorUpdate;
            PureAudioEngine.OnLogUpdated += OnLogUpdated;
            PureAudioEngine.OnWarningUpdated += OnWarningUpdated;
            OnLogUpdated(); // シーン移行や初期化時のキャッチアップ
            OnWarningUpdated();
        }

        private void OnDisable()
        {
            EditorApplication.update -= OnEditorUpdate;
            PureAudioEngine.OnLogUpdated -= OnLogUpdated;
            PureAudioEngine.OnWarningUpdated -= OnWarningUpdated;
        }

        private void OnWarningUpdated()
        {
            if (PureAudioEngine.Instance != null)
            {
                var engineWarnings = PureAudioEngine.WarningHistory;
                foreach (var warn in engineWarnings)
                {
                    bool alreadyCached = false;
                    for (int i = _cachedWarnings.Count - 1; i >= 0; i--)
                    {
                        var cached = _cachedWarnings[i];
                        if (Mathf.Approximately(cached.timestamp, warn.timestamp) && 
                            cached.eventType == warn.eventType && 
                            cached.cueName == warn.cueName && 
                            cached.message == warn.message)
                        {
                            alreadyCached = true;
                            break;
                        }
                    }
                    if (!alreadyCached)
                    {
                        _cachedWarnings.Add(warn);
                        if (_cachedWarnings.Count > 100)
                        {
                            _cachedWarnings.RemoveAt(0);
                        }
                    }
                }
            }

            if (_autoScrollWarnings)
            {
                _warningScrollPos.y = float.MaxValue;
            }
            Repaint();
        }

        private void OnLogUpdated()
        {
            if (PureAudioEngine.Instance != null)
            {
                var engineLogs = PureAudioEngine.LogHistory;
                foreach (var log in engineLogs)
                {
                    bool alreadyCached = false;
                    for (int i = _cachedLogs.Count - 1; i >= 0; i--)
                    {
                        var cached = _cachedLogs[i];
                        if (Mathf.Approximately(cached.timestamp, log.timestamp) && 
                            cached.eventType == log.eventType && 
                            cached.cueName == log.cueName && 
                            cached.message == log.message)
                        {
                            alreadyCached = true;
                            break;
                        }
                    }
                    if (!alreadyCached)
                    {
                        _cachedLogs.Add(log);
                        if (_cachedLogs.Count > 100)
                        {
                            _cachedLogs.RemoveAt(0);
                        }
                    }
                }
            }

            if (_autoScrollLogs)
            {
                _logScrollPos.y = float.MaxValue;
            }
            Repaint();
        }

        private void OnEditorUpdate()
        {
            if (EditorApplication.isPlaying && EditorApplication.timeSinceStartup - _lastRepaintTime > 0.1f)
            {
                Repaint();
                _lastRepaintTime = EditorApplication.timeSinceStartup;
            }
        }

        private void OnGUI()
        {
            DrawHeader();

            if (!EditorApplication.isPlaying)
            {
                EditorGUILayout.HelpBox("Play Mode停止中。最後に記録されたログを表示しています。", MessageType.Info);
                
                using (new EditorGUILayout.HorizontalScope("box"))
                {
                    EditorGUILayout.LabelField("プロファイリングを開始するにはゲームを再生してください。");
                    if (GUILayout.Button("Play Mode 開始", GUILayout.Width(120)))
                    {
                        EditorApplication.isPlaying = true;
                    }
                }
                EditorGUILayout.Space(10);
            }
            else if (PureAudioEngine.Instance == null)
            {
                EditorGUILayout.HelpBox("PureAudioEngine インスタンスが見つかりません。シーン内に配置してください。", MessageType.Warning);
                return;
            }

            if (EditorApplication.isPlaying && PureAudioEngine.Instance != null)
            {
                DrawDashboard();
                EditorGUILayout.Space(10);

                DrawCategoryMixer();
                EditorGUILayout.Space(10);

                DrawVoiceMonitor();
                EditorGUILayout.Space(10);

                DrawLoudnessMeters();
                EditorGUILayout.Space(10);
            }
            else
            {
                // 非PlayMode時のフォールバック表示
                GUILayout.Label("Dashboard", EditorStyles.boldLabel);
                using (new EditorGUILayout.HorizontalScope("box"))
                {
                    EditorGUILayout.LabelField("Voice Pool Usage: Play Mode停止中");
                    EditorGUILayout.LabelField("Est. Memory: -");
                    EditorGUILayout.LabelField("Engine Update CPU: -");
                }
                EditorGUILayout.Space(10);
                
                GUILayout.Label("Active Voices Monitor (Play Mode停止中)", EditorStyles.boldLabel);
                using (new EditorGUILayout.VerticalScope("box"))
                {
                    EditorGUILayout.LabelField("アクティブなボイスはありません。");
                }
                EditorGUILayout.Space(10);
            }

            // 下部タブ切り替えUI
            _activeTab = GUILayout.Toolbar(_activeTab, new string[] { "Log Monitor", "Warning Monitor", "Profiler Settings" });
            EditorGUILayout.Space(5);

            if (_activeTab == 0)
            {
                DrawLogMonitor();
            }
            else if (_activeTab == 1)
            {
                DrawWarningMonitor();
            }
            else if (_activeTab == 2)
            {
                DrawSettingsTab();
            }
        }

        private void DrawHeader()
        {
            GUILayout.BeginHorizontal(EditorStyles.toolbar);
            GUILayout.Label("PureAudio Profiler (Unity 6 Optimized)", EditorStyles.miniLabel);
            GUILayout.FlexibleSpace();
            if (EditorApplication.isPlaying && PureAudioEngine.Instance != null)
            {
                if (GUILayout.Button("Pause All", EditorStyles.toolbarButton))
                {
                    PureAudioEngine.Instance.PauseAll();
                }
                if (GUILayout.Button("Resume All", EditorStyles.toolbarButton))
                {
                    PureAudioEngine.Instance.ResumeAll();
                }
                if (GUILayout.Button("Stop All", EditorStyles.toolbarButton))
                {
                    PureAudioEngine.Instance.StopAll();
                }
            }
            
            // Clear Logs は停止中でも動作可能
            if (GUILayout.Button("Clear Logs", EditorStyles.toolbarButton))
            {
                if (PureAudioEngine.Instance != null)
                {
                    PureAudioEngine.LogHistory.Clear();
                    PureAudioEngine.WarningHistory.Clear();
                }
                _cachedLogs.Clear();
                _cachedWarnings.Clear();
                _selectedLogIndex = -1;
                _selectedWarningIndex = -1;
                Repaint();
            }
            GUILayout.EndHorizontal();
        }

        private void DrawNotPlayingWarning()
        {
            EditorGUILayout.Space(20);
            using (new EditorGUILayout.VerticalScope("box"))
            {
                EditorGUILayout.LabelField("PureAudio Profiler", EditorStyles.boldLabel);
                EditorGUILayout.HelpBox("プロファイリングを開始するには、Unity エディタを再生モード（Play Mode）にしてください。", MessageType.Info);
                
                EditorGUILayout.Space(10);
                if (GUILayout.Button("Play Mode を開始", GUILayout.Height(30)))
                {
                    EditorApplication.isPlaying = true;
                }
            }
        }

        private void DrawDashboard()
        {
            var engine = PureAudioEngine.Instance;
            var pool = engine.GetVoicePool();
            
            int activeVoices = 0;
            int loadingVoices = 0;
            foreach (var voice in pool)
            {
                if (voice.State == VoiceState.Playing || voice.State == VoiceState.Paused)
                    activeVoices++;
                else if (voice.State == VoiceState.Loading)
                    loadingVoices++;
            }

            long memoryBytes = engine.GetEstimatedMemoryBytes();
            double cpuTime = engine.UpdateCpuTimeMs;

            // アクティブな AudioListener 情報の取得
            AudioListener listener = null;
            string listenerGoName = "None";
            string listenerPosition = "-";
            string listenerComponents = "-";

            var listeners = UnityEngine.Object.FindObjectsByType<AudioListener>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            if (listeners != null && listeners.Length > 0)
            {
                listener = listeners[0];
                listenerGoName = listener.gameObject.name;
                listenerPosition = listener.transform.position.ToString("F2");

                var comps = listener.GetComponents<Component>();
                var compNames = new List<string>();
                foreach (var c in comps)
                {
                    if (c != null)
                    {
                        compNames.Add(c.GetType().Name);
                    }
                }
                listenerComponents = string.Join(", ", compNames);
            }

            GUILayout.Label("Dashboard", EditorStyles.boldLabel);
            using (new EditorGUILayout.VerticalScope("box"))
            {
                // 1段目: システムステータス (Voice, Memory, CPU)
                using (new EditorGUILayout.HorizontalScope())
                {
                    // ボイス使用状況
                    using (new EditorGUILayout.VerticalScope(GUILayout.Width(200)))
                    {
                        EditorGUILayout.LabelField("Voice Pool Usage", EditorStyles.miniBoldLabel);
                        float ratio = (float)activeVoices / engine.poolSize;
                        Rect r = EditorGUILayout.GetControlRect(false, 20);
                        EditorGUI.ProgressBar(r, ratio, $"{activeVoices} / {engine.poolSize}");
                    }

                    GUILayout.Space(25);

                    // メモリ使用状況
                    using (new EditorGUILayout.VerticalScope(GUILayout.Width(180)))
                    {
                        EditorGUILayout.LabelField("Clip Est. Memory", EditorStyles.miniBoldLabel);
                        string memStr = memoryBytes > 1024 * 1024 
                            ? $"{(double)memoryBytes / (1024 * 1024):F2} MB" 
                            : $"{(double)memoryBytes / 1024:F1} KB";
                        EditorGUILayout.LabelField(memStr, EditorStyles.largeLabel);
                    }

                    GUILayout.Space(25);

                    // CPU負荷
                    using (new EditorGUILayout.VerticalScope(GUILayout.Width(150)))
                    {
                        EditorGUILayout.LabelField("Engine Update CPU", EditorStyles.miniBoldLabel);
                        EditorGUILayout.LabelField($"{cpuTime:F3} ms", EditorStyles.largeLabel);
                    }
                    
                    GUILayout.FlexibleSpace();
                }

                EditorGUILayout.Space(5);
                // 軽量な水平区切り線を描画
                Rect lineRect = GUILayoutUtility.GetRect(10, 1, GUILayout.ExpandWidth(true));
                EditorGUI.DrawRect(lineRect, new Color(0.3f, 0.3f, 0.3f, 0.3f));
                EditorGUILayout.Space(5);

                // 2段目: アクティブな AudioListener 情報
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUILayout.VerticalScope())
                    {
                        EditorGUILayout.LabelField("Active Audio Listener", EditorStyles.miniBoldLabel);
                        
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            EditorGUILayout.LabelField("GameObject:", GUILayout.Width(75));
                            GUIStyle boldStyle = new GUIStyle(EditorStyles.boldLabel);
                            boldStyle.normal.textColor = listener != null ? Color.green : Color.red;
                            EditorGUILayout.LabelField(listenerGoName, boldStyle, GUILayout.Width(150));

                            EditorGUILayout.LabelField("Position:", GUILayout.Width(55));
                            EditorGUILayout.LabelField(listenerPosition, GUILayout.Width(120));
                        }
                        
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            EditorGUILayout.LabelField("Components:", GUILayout.Width(75));
                            GUIStyle compStyle = new GUIStyle(EditorStyles.miniLabel);
                            compStyle.wordWrap = true;
                            EditorGUILayout.LabelField(listenerComponents, compStyle);
                        }
                    }
                }
            }
        }

        private void DrawCategoryMixer()
        {
            var engine = PureAudioEngine.Instance;
            if (engine.categories.Count == 0) return;

            GUILayout.Label("Category Mixer (Hierarchical)", EditorStyles.boldLabel);
            using (new EditorGUILayout.VerticalScope("box"))
            {
                // テーブルヘッダー
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField("Category Name", EditorStyles.miniBoldLabel, GUILayout.Width(200));
                    EditorGUILayout.LabelField("Voices", EditorStyles.miniBoldLabel, GUILayout.Width(60));
                    EditorGUILayout.LabelField("Volume", EditorStyles.miniBoldLabel, GUILayout.Width(160));
                    EditorGUILayout.LabelField("Final Vol", EditorStyles.miniBoldLabel, GUILayout.Width(60));
                    EditorGUILayout.LabelField("Mute", EditorStyles.miniBoldLabel, GUILayout.Width(40));
                    EditorGUILayout.LabelField("Pause", EditorStyles.miniBoldLabel, GUILayout.Width(45));
                    EditorGUILayout.LabelField("AudioMixerGroup", EditorStyles.miniBoldLabel, GUILayout.MinWidth(100));
                    GUILayout.FlexibleSpace();
                }

                EditorGUILayout.Separator();

                // ルートカテゴリ（親がいないカテゴリ）のみを抽出し、再帰的に描画
                HashSet<PureAudioCategory> drawnCategories = new HashSet<PureAudioCategory>();

                foreach (var category in engine.categories)
                {
                    if (category != null && category.parentCategory == null)
                    {
                        DrawCategoryRow(category, 0, drawnCategories);
                    }
                }

                // 孤立したカテゴリ（親が登録リストに含まれておらず、ルート扱いになっていないカテゴリ）のフォールバック描画
                foreach (var category in engine.categories)
                {
                    if (category != null && !drawnCategories.Contains(category))
                    {
                        DrawCategoryRow(category, 0, drawnCategories);
                    }
                }
            }
        }

        private void DrawCategoryRow(PureAudioCategory category, int depth, HashSet<PureAudioCategory> drawnCategories)
        {
            if (category == null || drawnCategories.Contains(category)) return;
            drawnCategories.Add(category);

            var engine = PureAudioEngine.Instance;

            using (new EditorGUILayout.HorizontalScope())
            {
                // インデント表現
                string indent = new string(' ', depth * 4);
                string prefix = depth > 0 ? "└ " : "";
                EditorGUILayout.LabelField(indent + prefix + category.categoryName, GUILayout.Width(200));
                
                string limitStr = category.maxVoices > 0 ? $"{category.ActiveVoiceCount}/{category.maxVoices}" : $"{category.ActiveVoiceCount}";
                EditorGUILayout.LabelField(limitStr, GUILayout.Width(60));

                // 個別音量スライダー
                float prevVol = category.RuntimeVolume;
                float newVol = EditorGUILayout.Slider(prevVol, 0f, 1f, GUILayout.Width(160));
                if (!Mathf.Approximately(prevVol, newVol))
                {
                    category.RuntimeVolume = newVol;
                }

                // 計算後の最終実効音量（親階層を乗算した結果）
                float finalVol = category.GetFinalVolume();
                EditorGUILayout.LabelField($"{finalVol:F2}", GUILayout.Width(60));

                // ミュート
                bool prevMute = category.isMuted;
                bool newMute = EditorGUILayout.Toggle(prevMute, GUILayout.Width(40));
                if (prevMute != newMute)
                {
                    category.isMuted = newMute;
                }

                // ポーズ
                bool prevPause = category.isPaused;
                bool newPause = EditorGUILayout.Toggle(prevPause, GUILayout.Width(45));
                if (prevPause != newPause)
                {
                    if (newPause)
                        engine.PauseCategory(category);
                    else
                        engine.ResumeCategory(category);
                }

                // AudioMixerGroup
                string mixerName = category.unityMixerGroup != null ? category.unityMixerGroup.name : "[Default]";
                EditorGUILayout.LabelField(mixerName, EditorStyles.miniLabel, GUILayout.MinWidth(100));

                GUILayout.FlexibleSpace();
            }

            // 子カテゴリを探して描画
            foreach (var child in engine.categories)
            {
                if (child != null && child.parentCategory == category)
                {
                    DrawCategoryRow(child, depth + 1, drawnCategories);
                }
            }
        }

        private void DrawVoiceMonitor()
        {
            var engine = PureAudioEngine.Instance;
            var pool = engine.GetVoicePool();

            GUILayout.Label("Active Voices Monitor", EditorStyles.boldLabel);
            
            using (new EditorGUILayout.VerticalScope("box"))
            {
                // テーブルヘッダー
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField("ID", EditorStyles.miniBoldLabel, GUILayout.Width(30));
                    EditorGUILayout.LabelField("Voice State", EditorStyles.miniBoldLabel, GUILayout.Width(80));
                    EditorGUILayout.LabelField("Env State", EditorStyles.miniBoldLabel, GUILayout.Width(70));
                    EditorGUILayout.LabelField("Cue Name", EditorStyles.miniBoldLabel, GUILayout.Width(130));
                    EditorGUILayout.LabelField("AudioClip Name", EditorStyles.miniBoldLabel, GUILayout.Width(130));
                    EditorGUILayout.LabelField("Vol (dB)", EditorStyles.miniBoldLabel, GUILayout.Width(60));
                    EditorGUILayout.LabelField("Position / Target", EditorStyles.miniBoldLabel, GUILayout.Width(130));
                    EditorGUILayout.LabelField("Time/Length", EditorStyles.miniBoldLabel, GUILayout.Width(80));
                    GUILayout.FlexibleSpace();
                }

                EditorGUILayout.Separator();

                using (var scroll = new EditorGUILayout.ScrollViewScope(_voiceScrollPos, GUILayout.Height(150)))
                {
                    _voiceScrollPos = scroll.scrollPosition;

                    foreach (var voice in pool)
                    {
                        if (voice == null || voice.State == VoiceState.Free) continue;

                        using (new EditorGUILayout.HorizontalScope())
                        {
                            EditorGUILayout.LabelField(voice.VoiceId.ToString(), GUILayout.Width(30));
                            
                            // 状態色付け
                            GUIStyle stateStyle = new GUIStyle(EditorStyles.label);
                            if (voice.State == VoiceState.Loading) stateStyle.normal.textColor = Color.yellow;
                            else if (voice.State == VoiceState.Playing) stateStyle.normal.textColor = Color.green;
                            else if (voice.State == VoiceState.Paused) stateStyle.normal.textColor = Color.cyan;
                            EditorGUILayout.LabelField(voice.State.ToString(), stateStyle, GUILayout.Width(80));

                            // ADSR エンベロープ状態
                            GUIStyle envStyle = new GUIStyle(EditorStyles.label);
                            if (voice.EnvState == EnvelopeState.Attack) envStyle.normal.textColor = Color.green;
                            else if (voice.EnvState == EnvelopeState.Decay) envStyle.normal.textColor = new Color(1f, 0.7f, 0f);
                            else if (voice.EnvState == EnvelopeState.Sustain) envStyle.normal.textColor = Color.white;
                            else if (voice.EnvState == EnvelopeState.Release) envStyle.normal.textColor = Color.red;
                            EditorGUILayout.LabelField(voice.EnvState.ToString(), envStyle, GUILayout.Width(70));

                            EditorGUILayout.LabelField(voice.CurrentCue != null ? voice.CurrentCue.cueName : "-", GUILayout.Width(130));
                            EditorGUILayout.LabelField(voice.CurrentClip != null ? voice.CurrentClip.name : "-", GUILayout.Width(130));

                            // 音量表示 (dB)
                            AudioSource src = voice.GetComponent<AudioSource>();
                            float vol = src != null ? src.volume : 0f;
                            float db = LinearToDb(vol);
                            EditorGUILayout.LabelField($"{db:F1} dB", GUILayout.Width(60));

                            // 位置
                            string posStr = src != null && src.spatialBlend > 0f 
                                ? (voice.gameObject.transform.position.ToString("F1")) 
                                : "2D (Global)";
                            EditorGUILayout.LabelField(posStr, GUILayout.Width(130));

                            // 時間
                            string timeStr = "-";
                            if (voice.State == VoiceState.Playing && src != null && src.clip != null)
                            {
                                timeStr = $"{src.time:F1}s / {src.clip.length:F1}s";
                            }
                            EditorGUILayout.LabelField(timeStr, GUILayout.Width(80));

                            GUILayout.FlexibleSpace();
                        }
                    }
                }
            }
        }

        private void DrawLogMonitor()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Log Monitor (Recent 100 entries - Click to select)", EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            _autoScrollLogs = GUILayout.Toggle(_autoScrollLogs, "Auto Scroll Logs", GUILayout.Width(120));
            GUILayout.EndHorizontal();

            using (new EditorGUILayout.VerticalScope("box"))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField("Time", EditorStyles.miniBoldLabel, GUILayout.Width(60));
                    EditorGUILayout.LabelField("Type", EditorStyles.miniBoldLabel, GUILayout.Width(70));
                    EditorGUILayout.LabelField("Cue Name", EditorStyles.miniBoldLabel, GUILayout.Width(120));
                    EditorGUILayout.LabelField("Message", EditorStyles.miniBoldLabel, GUILayout.MinWidth(250));
                    GUILayout.FlexibleSpace();
                }

                EditorGUILayout.Separator();

                using (var scroll = new EditorGUILayout.ScrollViewScope(_logScrollPos, GUILayout.Height(120)))
                {
                    _logScrollPos = scroll.scrollPosition;

                    var logs = _cachedLogs;
                    for (int i = 0; i < logs.Count; i++)
                    {
                        var entry = logs[i];
                        bool isSelected = (_selectedLogIndex == i);

                        var rect = EditorGUILayout.BeginHorizontal();
                        
                        // 選択状態のハイライト背景描画
                        if (isSelected)
                        {
                            GUI.Box(rect, "", (GUIStyle)"selectionRect");
                        }

                        EditorGUILayout.LabelField(entry.timestamp.ToString("F2") + "s", GUILayout.Width(60));
                        
                        GUIStyle typeStyle = new GUIStyle(EditorStyles.label);
                        if (entry.eventType == "PLAY") typeStyle.normal.textColor = Color.green;
                        else if (entry.eventType == "STEAL") typeStyle.normal.textColor = new Color(1f, 0.5f, 0f);
                        else if (entry.eventType == "PREVENT") typeStyle.normal.textColor = Color.red;
                        else if (entry.eventType == "WARN") typeStyle.normal.textColor = Color.yellow;
                        else if (entry.eventType == "SYSTEM") typeStyle.normal.textColor = Color.cyan;

                        EditorGUILayout.LabelField(entry.eventType, typeStyle, GUILayout.Width(70));
                        EditorGUILayout.LabelField(entry.cueName, GUILayout.Width(120));
                        EditorGUILayout.LabelField(entry.message, GUILayout.MinWidth(250));
                        GUILayout.FlexibleSpace();

                        EditorGUILayout.EndHorizontal();

                        // 行クリックの判定
                        if (Event.current.type == EventType.MouseDown && rect.Contains(Event.current.mousePosition))
                        {
                            _selectedLogIndex = i;
                            Repaint();
                            Event.current.Use();
                        }
                    }
                }
            }

            // 選択されたログのスタックトレース表示
            if (_selectedLogIndex >= 0 && _selectedLogIndex < _cachedLogs.Count)
            {
                var selectedLog = _cachedLogs[_selectedLogIndex];
                EditorGUILayout.Space(5);
                GUILayout.Label("Selected Log Call Stack (Double click to jump to C# script)", EditorStyles.boldLabel);

                using (new EditorGUILayout.VerticalScope("box"))
                {
                    using (var stackScroll = new EditorGUILayout.ScrollViewScope(_stackScrollPos, GUILayout.Height(120)))
                    {
                        _stackScrollPos = stackScroll.scrollPosition;

                        if (string.IsNullOrEmpty(selectedLog.stackTrace))
                        {
                            GUILayout.Label("コールスタック情報がありません。(Editor外で出力されたログなど)", EditorStyles.miniLabel);
                        }
                        else
                        {
                            string[] frames = selectedLog.stackTrace.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                            foreach (var frame in frames)
                            {
                                string trimmedFrame = frame.Trim();
                                if (string.IsNullOrEmpty(trimmedFrame)) continue;

                                // ログシステム自身の内部呼び出しはノイズになるため表示をスキップ
                                if (trimmedFrame.Contains("PureAudioEngine.AddLog") ||
                                    trimmedFrame.Contains("System.Environment.get_StackTrace") ||
                                    trimmedFrame.Contains("System.Diagnostics.StackTrace"))
                                {
                                    continue;
                                }

                                Rect frameRect = EditorGUILayout.GetControlRect(false, 18);
                                GUI.Label(frameRect, trimmedFrame, EditorStyles.miniLabel);

                                // ダブルクリックでジャンプ
                                if (Event.current.type == EventType.MouseDown && frameRect.Contains(Event.current.mousePosition))
                                {
                                    if (Event.current.clickCount == 2)
                                    {
                                        OpenStackFrame(trimmedFrame);
                                        Event.current.Use();
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        private void OpenStackFrame(string frameString)
        {
            // Windowsの一般的なコールスタック形式:
            // "   at Unity.FPS.Game.WeaponController.HandleShoot () [0x00010] in Z:\UnityAudioTest\3D_PureFPS\Assets\FPS\Scripts\Game\Shared\WeaponController.cs:493"
            int inIdx = frameString.IndexOf(" in ");
            if (inIdx < 0) return;

            string pathAndLine = frameString.Substring(inIdx + 4).Trim();
            int colonIdx = pathAndLine.LastIndexOf(':');
            if (colonIdx < 0) return;

            string filePath = pathAndLine.Substring(0, colonIdx);
            string lineStr = pathAndLine.Substring(colonIdx + 1);

            if (lineStr.StartsWith("line "))
            {
                lineStr = lineStr.Substring(5).Trim();
            }

            if (int.TryParse(lineStr, out int line))
            {
                filePath = filePath.Replace('\\', '/');
                string projDir = System.IO.Directory.GetCurrentDirectory().Replace('\\', '/');

                if (filePath.StartsWith(projDir))
                {
                    filePath = filePath.Substring(projDir.Length).TrimStart('/');
                }

                // スクリプトアセットを直接開く
                var asset = AssetDatabase.LoadAssetAtPath<MonoScript>(filePath);
                if (asset != null)
                {
                    AssetDatabase.OpenAsset(asset, line);
                }
                else
                {
                    var obj = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(filePath);
                    if (obj != null)
                    {
                        AssetDatabase.OpenAsset(obj, line);
                    }
                    else
                    {
                        Debug.LogWarning($"[PureAudio Profiler] Could not open asset at path: {filePath}");
                    }
                }
            }
        }

        private void DrawLoudnessMeters()
        {
            _showLoudnessMeter = EditorGUILayout.Foldout(_showLoudnessMeter, "Loudness Level Meters", true);
            if (!_showLoudnessMeter) return;

            using (new EditorGUILayout.VerticalScope("box"))
            {
                // 1. Master Output (AudioListener から取得)
                float masterRms = 0f;
                try
                {
                    AudioListener.GetOutputData(_sampleBuffer, 0);
                    masterRms = CalculateRms(_sampleBuffer);
                }
                catch
                {
                    // AudioListenerがシーンに存在しない、またはアクティブでない場合の例外防止
                }
                float masterDb = RmsToDb(masterRms);
                DrawMeterBar("Master Output (Listener)", masterDb);

                EditorGUILayout.Space(5);

                // 2. Category Output (表示・非表示のトグル付き)
                _showCategoryLoudness = EditorGUILayout.Toggle("Show Category Output Meters", _showCategoryLoudness);
                if (_showCategoryLoudness && PureAudioEngine.Instance != null)
                {
                    var engine = PureAudioEngine.Instance;
                    var pool = engine.GetVoicePool();

                    foreach (var category in engine.categories)
                    {
                        if (category == null) continue;

                        float catMaxRms = 0f;
                        // このカテゴリに属するアクティブなボイスから最大RMSを取得
                        foreach (var voice in pool)
                        {
                            if (voice != null && voice.State == VoiceState.Playing && voice.CurrentCue != null &&
                                (voice.CurrentCue.category == category || IsSubcategoryOf(voice.CurrentCue.category, category)))
                            {
                                var src = voice.GetComponent<AudioSource>();
                                if (src != null && src.isPlaying)
                                {
                                    float[] voiceSamples = new float[64];
                                    src.GetOutputData(voiceSamples, 0);
                                    float voiceRms = CalculateRms(voiceSamples);
                                    if (voiceRms > catMaxRms)
                                    {
                                        catMaxRms = voiceRms;
                                    }
                                }
                            }
                        }

                        float catDb = RmsToDb(catMaxRms);
                        DrawMeterBar($"  └ {category.categoryName}", catDb);
                    }
                }
            }
        }

        private void DrawMeterBar(string label, float db)
        {
            float minDb = -60f;
            float maxDb = 0f;
            float progress = Mathf.Clamp01((db - minDb) / (maxDb - minDb));

            Rect rect = EditorGUILayout.GetControlRect(true, 18);
            
            // ラベルの描画
            Rect labelRect = new Rect(rect.x, rect.y, 160, rect.height);
            GUI.Label(labelRect, label, EditorStyles.miniLabel);

            // メーターバーの描画
            Rect barRect = new Rect(rect.x + 160, rect.y, rect.width - 160, rect.height);
            
            // 音量に応じたカラー選定 (緑 ➔ 黄 ➔ 赤)
            Color meterColor = Color.green;
            if (db > -10f) meterColor = Color.red;
            else if (db > -20f) meterColor = new Color(1f, 0.7f, 0f);

            var prevColor = GUI.color;
            GUI.color = meterColor;
            string dbText = db <= -79.9f ? "-∞ dB" : $"{db:F1} dB";
            EditorGUI.ProgressBar(barRect, progress, dbText);
            GUI.color = prevColor;
        }

        private float CalculateRms(float[] samples)
        {
            if (samples == null || samples.Length == 0) return 0f;
            float sum = 0f;
            for (int i = 0; i < samples.Length; i++)
            {
                sum += samples[i] * samples[i];
            }
            return Mathf.Sqrt(sum / samples.Length);
        }

        private float RmsToDb(float rms)
        {
            if (rms <= 0.0001f) return -80.0f;
            return 20f * Mathf.Log10(rms);
        }

        private bool IsSubcategoryOf(PureAudioCategory child, PureAudioCategory parent)
        {
            if (child == null || parent == null) return false;
            PureAudioCategory curr = child.parentCategory;
            while (curr != null)
            {
                if (curr == parent) return true;
                curr = curr.parentCategory;
            }
            return false;
        }

        private float LinearToDb(float linear)
        {
            if (linear <= 0.0001f) return -80.0f;
            return 20f * Mathf.Log10(linear);
        }

        private void DrawWarningMonitor()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Warning Monitor (Recent 100 entries - Click to select)", EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            _includeCallStackInExport = GUILayout.Toggle(_includeCallStackInExport, "Include Callstacks in TXT", GUILayout.Width(170));
            if (GUILayout.Button("Export Warnings (TXT)", GUILayout.Width(160)))
            {
                ExportWarningsToText();
            }
            _autoScrollWarnings = GUILayout.Toggle(_autoScrollWarnings, "Auto Scroll", GUILayout.Width(90));
            GUILayout.EndHorizontal();

            using (new EditorGUILayout.VerticalScope("box"))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField("Time", EditorStyles.miniBoldLabel, GUILayout.Width(60));
                    EditorGUILayout.LabelField("Type", EditorStyles.miniBoldLabel, GUILayout.Width(100));
                    EditorGUILayout.LabelField("Cue Name", EditorStyles.miniBoldLabel, GUILayout.Width(120));
                    EditorGUILayout.LabelField("Message", EditorStyles.miniBoldLabel, GUILayout.MinWidth(250));
                    GUILayout.FlexibleSpace();
                }

                EditorGUILayout.Separator();

                using (var scroll = new EditorGUILayout.ScrollViewScope(_warningScrollPos, GUILayout.Height(120)))
                {
                    _warningScrollPos = scroll.scrollPosition;

                    var warnings = _cachedWarnings;
                    for (int i = 0; i < warnings.Count; i++)
                    {
                        var entry = warnings[i];
                        bool isSelected = (_selectedWarningIndex == i);

                        var rect = EditorGUILayout.BeginHorizontal();
                        
                        if (isSelected)
                        {
                            GUI.Box(rect, "", (GUIStyle)"selectionRect");
                        }

                        EditorGUILayout.LabelField(entry.timestamp.ToString("F2") + "s", GUILayout.Width(60));
                        
                        GUIStyle typeStyle = new GUIStyle(EditorStyles.label);
                        if (entry.eventType == "WARN_LIMIT") typeStyle.normal.textColor = new Color(1f, 0.5f, 0f);
                        else if (entry.eventType == "WARN_VOLUME") typeStyle.normal.textColor = Color.red;
                        else typeStyle.normal.textColor = Color.yellow;

                        EditorGUILayout.LabelField(entry.eventType, typeStyle, GUILayout.Width(100));
                        EditorGUILayout.LabelField(entry.cueName, GUILayout.Width(120));
                        EditorGUILayout.LabelField(entry.message, GUILayout.MinWidth(250));
                        GUILayout.FlexibleSpace();

                        EditorGUILayout.EndHorizontal();

                        if (Event.current.type == EventType.MouseDown && rect.Contains(Event.current.mousePosition))
                        {
                            _selectedWarningIndex = i;
                            Repaint();
                            Event.current.Use();
                        }
                    }
                }
            }

            if (_selectedWarningIndex >= 0 && _selectedWarningIndex < _cachedWarnings.Count)
            {
                var selectedLog = _cachedWarnings[_selectedWarningIndex];
                EditorGUILayout.Space(5);
                GUILayout.Label("Selected Warning Call Stack (Double click to jump to C# script)", EditorStyles.boldLabel);

                using (new EditorGUILayout.VerticalScope("box"))
                {
                    using (var stackScroll = new EditorGUILayout.ScrollViewScope(_warningStackScrollPos, GUILayout.Height(120)))
                    {
                        _warningStackScrollPos = stackScroll.scrollPosition;

                        if (string.IsNullOrEmpty(selectedLog.stackTrace))
                        {
                            GUILayout.Label("コールスタック情報がありません。", EditorStyles.miniLabel);
                        }
                        else
                        {
                            string[] frames = selectedLog.stackTrace.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                            foreach (var frame in frames)
                            {
                                string trimmedFrame = frame.Trim();
                                if (string.IsNullOrEmpty(trimmedFrame)) continue;

                                if (trimmedFrame.Contains("PureAudioEngine.AddWarning") ||
                                    trimmedFrame.Contains("PureAudioEngine.AddLog") ||
                                    trimmedFrame.Contains("System.Environment.get_StackTrace") ||
                                    trimmedFrame.Contains("System.Diagnostics.StackTrace"))
                                {
                                    continue;
                                }

                                Rect frameRect = EditorGUILayout.GetControlRect(false, 18);
                                GUI.Label(frameRect, trimmedFrame, EditorStyles.miniLabel);

                                if (Event.current.type == EventType.MouseDown && frameRect.Contains(Event.current.mousePosition))
                                {
                                    if (Event.current.clickCount == 2)
                                    {
                                        OpenStackFrame(trimmedFrame);
                                        Event.current.Use();
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        private void DrawSettingsTab()
        {
            GUILayout.Label("Profiler & Audio Engine Settings", EditorStyles.boldLabel);

            var engine = PureAudioEngine.Instance;
            if (engine == null)
            {
                EditorGUILayout.HelpBox("PureAudioEngine インスタンスがありません。設定できません。", MessageType.Info);
                return;
            }

            using (new EditorGUILayout.VerticalScope("box"))
            {
                EditorGUILayout.LabelField("Warning Trigger Settings", EditorStyles.miniBoldLabel);
                EditorGUILayout.Space(5);

                float prevVolThresh = engine.volumeWarningLevel;
                float volThreshDb = LinearToDb(prevVolThresh);
                
                GUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("Volume Warning Threshold", GUILayout.Width(180));
                float newVolThresh = EditorGUILayout.Slider(prevVolThresh, 0.0f, 1.2f);
                GUILayout.EndHorizontal();

                if (!Mathf.Approximately(prevVolThresh, newVolThresh))
                {
                    engine.volumeWarningLevel = newVolThresh;
                    EditorUtility.SetDirty(engine);
                }
                
                EditorGUILayout.LabelField($"  └ Current: {newVolThresh:F2} ({volThreshDb:F1} dB) (Warning triggers if voice output is higher)", EditorStyles.miniLabel);
                EditorGUILayout.Space(10);

                float prevDuration = engine.shortPlayDurationThreshold;
                float newDuration = EditorGUILayout.FloatField("Short-life Steal Limit (s)", prevDuration);
                if (!Mathf.Approximately(prevDuration, newDuration))
                {
                    engine.shortPlayDurationThreshold = Mathf.Max(0.1f, newDuration);
                    EditorUtility.SetDirty(engine);
                }
                EditorGUILayout.LabelField("  └ Triggers warning if a voice is stolen within this duration (seconds) from trigger", EditorStyles.miniLabel);

                // メモリ警告閾値
                EditorGUILayout.Space(10);
                long prevMemThresh = engine.memoryWarningLevelMB;
                long newMemThresh = EditorGUILayout.LongField("Memory Warning Threshold (MB)", prevMemThresh);
                if (prevMemThresh != newMemThresh)
                {
                    engine.memoryWarningLevelMB = Mathf.Max(0, (int)newMemThresh);
                    EditorUtility.SetDirty(engine);
                }
                EditorGUILayout.LabelField("  └ Triggers warning if estimated clip memory exceeds this value (0 is disabled)", EditorStyles.miniLabel);
            }
        }

        private void ExportWarningsToText()
        {
            string defaultName = $"PureAudio_Warnings_{DateTime.Now:yyyyMMdd_HHmmss}";
            string path = EditorUtility.SaveFilePanel("Export Warning Logs", "", defaultName, "txt");
            if (string.IsNullOrEmpty(path)) return;

            try
            {
                var warnings = _cachedWarnings;
                using (var writer = new System.IO.StreamWriter(path, false, System.Text.Encoding.UTF8))
                {
                    writer.WriteLine("================================================================================");
                    writer.WriteLine($"PureAudio Warning Logs Export - {DateTime.Now}");
                    writer.WriteLine("================================================================================");
                    writer.WriteLine();

                    if (warnings.Count == 0)
                    {
                        writer.WriteLine("No warning logs recorded in cache.");
                    }
                    else
                    {
                        foreach (var warn in warnings)
                        {
                            writer.WriteLine($"[{warn.timestamp:F2}s] [{warn.eventType}] Cue: {warn.cueName}");
                            writer.WriteLine($"Message: {warn.message}");
                            if (_includeCallStackInExport && !string.IsNullOrEmpty(warn.stackTrace))
                            {
                                writer.WriteLine("Call Stack:");
                                writer.WriteLine(warn.stackTrace);
                            }
                            writer.WriteLine("--------------------------------------------------------------------------------");
                        }
                    }
                }
                Debug.Log($"[PureAudio] Exported {warnings.Count} warnings to: {path}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[PureAudio] Failed to export warning logs: {ex.Message}");
            }
        }
    }
}
