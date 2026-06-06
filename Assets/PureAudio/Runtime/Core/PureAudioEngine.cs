using System.Collections.Generic;
using UnityEngine;
using System.Diagnostics;
using System;
using System.Collections;

namespace PureAudio
{
    /// <summary>
    /// PureAudioサウンドエンジンのコアマネージャー。
    /// PlaySE / PlayBGM / PlayVoice / PlayJingle / PlayAmb / PlayUI の各種APIを提供し、
    /// 話者制限、ダッキング、クロスフェード、ポーズ中再生、親子階層ミキサーを統括します。
    /// </summary>
    [DefaultExecutionOrder(-100)]
    public class PureAudioEngine : MonoBehaviour
    {
        public static PureAudioEngine Instance { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void AutoInitialize()
        {
            if (Instance == null)
            {
                GameObject go = new GameObject("[PureAudioEngine]");
                go.AddComponent<PureAudioEngine>();
            }
        }

        [Header("Pool Settings")]
        [Tooltip("ボイスプール内の最大同時再生可能数。")]
        public int poolSize = 64;

        [Header("Warning Settings")]
        [Tooltip("実効音量（0.0〜1.0）がこの閾値を超えたら警告を出します。")]
        public float volumeWarningLevel = 0.95f;

        [Tooltip("ボイススチール発生時、再生時間がこの秒数未満であれば『短寿命警告』を出します。")]
        public float shortPlayDurationThreshold = 1.0f;

        [Tooltip("推定オーディオクリップメモリ使用量（MB）がこの閾値を超えたら警告を出します。")]
        public long memoryWarningLevelMB = 50;

        [Header("Registered Data")]
        [Tooltip("プロジェクトで管理するサウンドカテゴリのリスト。")]
        public List<PureAudioCategory> categories = new List<PureAudioCategory>();

        [Tooltip("プロジェクト内のすべてのCueアセット")]
        public List<PureAudioCue> preloadedCues = new List<PureAudioCue>();

        [System.Serializable]
        public class PureAudioLog
        {
            public float timestamp;
            public string eventType; // PLAY, STOP, STEAL, PREVENT, WARN, SYSTEM, WARN_LIMIT, WARN_VOLUME
            public string cueName;
            public string message;
            [TextArea(3, 10)]
            public string stackTrace;
        }

        public static readonly List<PureAudioLog> LogHistory = new List<PureAudioLog>();
        public static event Action OnLogUpdated;

        public static readonly List<PureAudioLog> WarningHistory = new List<PureAudioLog>();
        public static event Action OnWarningUpdated;

        public static void AddLog(string eventType, string cueName, string message)
        {
            string trace = "";
#if UNITY_EDITOR
            // エディタ時のみスタックトレースを取得
            trace = System.Environment.StackTrace;
#endif

            var log = new PureAudioLog
            {
                timestamp = Time.time,
                eventType = eventType,
                cueName = cueName,
                message = message,
                stackTrace = trace
            };
            LogHistory.Add(log);
            if (LogHistory.Count > 100)
            {
                LogHistory.RemoveAt(0);
            }
            OnLogUpdated?.Invoke();
        }

        public static void AddWarning(string warnType, string cueName, string message)
        {
            string trace = "";
#if UNITY_EDITOR
            trace = System.Environment.StackTrace;
#endif

            var log = new PureAudioLog
            {
                timestamp = Time.time,
                eventType = warnType,
                cueName = cueName,
                message = message,
                stackTrace = trace
            };
            lock (WarningHistory)
            {
                WarningHistory.Add(log);
                if (WarningHistory.Count > 500)
                {
                    WarningHistory.RemoveAt(0);
                }
            }
            AddLog(warnType, cueName, message);
            OnWarningUpdated?.Invoke();
        }

        // ボイスプール
        private readonly List<PureAudioVoice> _voicePool = new List<PureAudioVoice>();

        // Modulationパラメータ管理
        private readonly Dictionary<string, float> _globalParameters = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

        // Cueのキャッシュ (名前 ➔ Cue)
        private readonly Dictionary<string, PureAudioCue> _cueCache = new Dictionary<string, PureAudioCue>(StringComparer.OrdinalIgnoreCase);

        // AudioClip直接再生時のTemp Cueキャッシュ
        private readonly Dictionary<AudioClip, PureAudioCue> _clipToCueCache = new Dictionary<AudioClip, PureAudioCue>();

        // Cueのプレイ要求履歴追跡 (名前 ➔ トリガー時刻のリスト)
        private readonly Dictionary<string, List<float>> _cueTriggerTimes = new Dictionary<string, List<float>>(StringComparer.OrdinalIgnoreCase);

        public void RecordCueTrigger(string cueName)
        {
            if (string.IsNullOrEmpty(cueName)) return;
            if (!_cueTriggerTimes.TryGetValue(cueName, out var times))
            {
                times = new List<float>();
                _cueTriggerTimes[cueName] = times;
            }
            times.Add(Time.time);
            
            // 3秒より古い履歴をクリーンアップ
            float thresholdTime = Time.time - 3.0f;
            times.RemoveAll(t => t < thresholdTime);
        }

        public int GetCueTriggerCountInLastSeconds(string cueName, float duration)
        {
            if (string.IsNullOrEmpty(cueName)) return 0;
            if (!_cueTriggerTimes.TryGetValue(cueName, out var times)) return 0;
            float thresholdTime = Time.time - duration;
            int count = 0;
            for (int i = 0; i < times.Count; i++)
            {
                if (times[i] >= thresholdTime) count++;
            }
            return count;
        }

        // プロファイリング情報
        public double UpdateCpuTimeMs { get; private set; }
        private Stopwatch _cpuStopwatch;

        // ジングルダッキング用変数
        private Coroutine _duckingCoroutine;
        private float _originalBgmVolume = 1f;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            DontDestroyOnLoad(gameObject);

            _cpuStopwatch = new Stopwatch();

            // Resources 内の全Cueアセットおよびカテゴリファイルを自動検出・ロード
            LoadAssetsFromResources();

            InitializePool();
            InitializeCueCache();

            // デフォルトで「BGM」と「SE」カテゴリを自動作成して追加
            CreateDefaultCategories();
        }

        private void LoadAssetsFromResources()
        {
            // Resources/PureAudio/Cues 内の全Cueアセットをロード
            PureAudioCue[] loadedCues = Resources.LoadAll<PureAudioCue>("PureAudio/Cues");
            foreach (var cue in loadedCues)
            {
                if (cue != null && !preloadedCues.Contains(cue))
                {
                    preloadedCues.Add(cue);
                }
            }

            // Resources/PureAudio/Categories 内の全カテゴリをロード
            PureAudioCategory[] loadedCategories = Resources.LoadAll<PureAudioCategory>("PureAudio/Categories");
            foreach (var cat in loadedCategories)
            {
                if (cat != null && !categories.Contains(cat))
                {
                    categories.Add(cat);
                }
            }

            // 親カテゴリが漏れていた場合の補正
            foreach (var cat in categories)
            {
                if (cat != null && cat.parentCategory != null && !categories.Contains(cat.parentCategory))
                {
                    categories.Add(cat.parentCategory);
                }
            }
        }

        private void OnEnable()
        {
            foreach (var category in categories)
            {
                if (category != null)
                {
                    category.ResetRuntimeState();
                }
            }
        }

        private void InitializePool()
        {
            GameObject poolContainer = new GameObject("[PureAudioPool]");
            poolContainer.transform.SetParent(transform);

            for (int i = 0; i < poolSize; i++)
            {
                GameObject voiceObj = new GameObject($"PureAudioVoice_{i}");
                voiceObj.transform.SetParent(poolContainer.transform);
                
                var voice = voiceObj.AddComponent<PureAudioVoice>();
                voice.VoiceId = i;
                voice.ResetVoice();
                
                _voicePool.Add(voice);
            }
        }

        private void InitializeCueCache()
        {
            _cueCache.Clear();
            foreach (var cue in preloadedCues)
            {
                RegisterCue(cue);
            }
        }

        private void CreateDefaultCategories()
        {
            if (categories == null) categories = new List<PureAudioCategory>();
            
            if (categories.Find(c => c != null && c.categoryName.Equals("BGM", StringComparison.OrdinalIgnoreCase)) == null)
            {
                var bgmCat = ScriptableObject.CreateInstance<PureAudioCategory>();
                bgmCat.categoryName = "BGM";
                bgmCat.volume = 1f;
                categories.Add(bgmCat);
            }
            
            if (categories.Find(c => c != null && c.categoryName.Equals("SE", StringComparison.OrdinalIgnoreCase)) == null)
            {
                var seCat = ScriptableObject.CreateInstance<PureAudioCategory>();
                seCat.categoryName = "SE";
                seCat.volume = 1f;
                seCat.maxVoices = 20; 
                categories.Add(seCat);
            }
        }

        /// <summary>
        /// Cueを登録します。
        /// </summary>
        public void RegisterCue(PureAudioCue cue)
        {
            if (cue == null || string.IsNullOrEmpty(cue.cueName)) return;
            _cueCache[cue.cueName] = cue;
        }

        private float _memoryCheckTimer = 0f;
        private float _lastMemoryWarningTime = -999f;

        private void CheckMemoryLimit(float deltaTime)
        {
            _memoryCheckTimer += deltaTime;
            if (_memoryCheckTimer >= 1.0f)
            {
                _memoryCheckTimer = 0f;
                long memoryBytes = GetEstimatedMemoryBytes();
                long limitBytes = memoryWarningLevelMB * 1024 * 1024;
                if (limitBytes > 0 && memoryBytes > limitBytes)
                {
                    if (Time.time - _lastMemoryWarningTime > 10.0f)
                    {
                        _lastMemoryWarningTime = Time.time;
                        double currentMB = (double)memoryBytes / (1024 * 1024);
                        AddWarning("WARN_MEMORY", "System", 
                            $"[Memory Limit Exceeded] Estimated audio clip memory {currentMB:F2} MB exceeds warning threshold of {memoryWarningLevelMB} MB.");
                    }
                }
            }
        }

        private void Update()
        {
            _cpuStopwatch.Restart();

            float deltaTime = Time.deltaTime;

            // カテゴリごとのアクティブボイス数をリセット
            foreach (var category in categories)
            {
                if (category != null) category.ActiveVoiceCount = 0;
            }

            // 各ボイスの更新
            foreach (var voice in _voicePool)
            {
                if (voice != null && voice.State != VoiceState.Free)
                {
                    voice.UpdateVoice(deltaTime);

                    // カテゴリごとの発音数をカウント
                    if (voice.State == VoiceState.Playing && voice.CurrentCue != null && voice.CurrentCue.category != null)
                    {
                        voice.CurrentCue.category.ActiveVoiceCount++;
                    }
                }
            }

            _cpuStopwatch.Stop();
            UpdateCpuTimeMs = _cpuStopwatch.Elapsed.TotalMilliseconds;

            CheckMemoryLimit(deltaTime);
        }

        #region Play / Stop / Pause API

        /// <summary>
        /// 効果音 (SE) を再生します（2D/3D対応）。
        /// </summary>
        public PureAudioVoice PlaySE(PureAudioCue cue, Transform followTarget = null, float volumeScale = 1f)
        {
            if (cue == null) return null;
            RecordCueTrigger(cue.cueName);

            PureAudioVoice voice = GetAvailableVoice(cue);
            if (voice == null) return null; 

            voice.Play(cue, followTarget, Vector3.zero, volumeScale, speaker: null, useUnscaledTime: false);
            AddLog("PLAY", cue.cueName, $"Started playing SE on Voice {voice.VoiceId}.");
            return voice;
        }

        /// <summary>
        /// 効果音 (SE) を名前指定で再生します。
        /// </summary>
        public PureAudioVoice PlaySE(string cueName, Transform followTarget = null, float volumeScale = 1f)
        {
            if (_cueCache.TryGetValue(cueName, out var cue))
            {
                return PlaySE(cue, followTarget, volumeScale);
            }
            AddLog("WARN", cueName, $"PlaySE failed: Cue not found.");
            return null;
        }

        /// <summary>
        /// 効果音 (SE) をAudioClip直接指定で再生します。
        /// </summary>
        public PureAudioVoice PlaySE(AudioClip clip, Transform followTarget = null, float volumeScale = 1f, string categoryName = "SE")
        {
            if (clip == null) return null;

            if (!_clipToCueCache.TryGetValue(clip, out var cue))
            {
                cue = ScriptableObject.CreateInstance<PureAudioCue>();
                cue.cueName = $"Temp_{clip.name}";
                cue.directAudioClips = new List<AudioClip> { clip };
                cue.volume = 1f;
                cue.pitch = 1f;

                PureAudioCategory cat = categories.Find(c => c != null && c.categoryName.Equals(categoryName, StringComparison.OrdinalIgnoreCase));
                if (cat == null && categories.Count > 0) cat = categories[0];
                cue.category = cat;

                _clipToCueCache[clip] = cue;
                RegisterCue(cue);
            }

            return PlaySE(cue, followTarget, volumeScale);
        }

        /// <summary>
        /// 効果音 (SE) を3D空間上の指定座標に固定して再生します。
        /// </summary>
        public PureAudioVoice PlaySE3D(PureAudioCue cue, Vector3 position, float volumeScale = 1f)
        {
            if (cue == null) return null;
            RecordCueTrigger(cue.cueName);

            PureAudioVoice voice = GetAvailableVoice(cue);
            if (voice == null) return null;

            voice.Play(cue, null, position, volumeScale, speaker: null, useUnscaledTime: false);
            AddLog("PLAY", cue.cueName, $"Started playing 3D SE on Voice {voice.VoiceId}.");
            return voice;
        }

        /// <summary>
        /// 効果音 (SE) を名前指定で3D空間上の指定座標に固定して再生します。
        /// </summary>
        public PureAudioVoice PlaySE3D(string cueName, Vector3 position, float volumeScale = 1f)
        {
            if (_cueCache.TryGetValue(cueName, out var cue))
            {
                return PlaySE3D(cue, position, volumeScale);
            }
            AddLog("WARN", cueName, $"PlaySE3D failed: Cue not found.");
            return null;
        }

        /// <summary>
        /// 効果音 (SE) をAudioClip直接指定で3D空間上の指定座標に固定して再生します。
        /// </summary>
        public PureAudioVoice PlaySE3D(AudioClip clip, Vector3 position, float volumeScale = 1f, string categoryName = "SE")
        {
            if (clip == null) return null;

            if (!_clipToCueCache.TryGetValue(clip, out var cue))
            {
                cue = ScriptableObject.CreateInstance<PureAudioCue>();
                cue.cueName = $"Temp_{clip.name}";
                cue.directAudioClips = new List<AudioClip> { clip };
                cue.volume = 1f;
                cue.pitch = 1f;

                PureAudioCategory cat = categories.Find(c => c != null && c.categoryName.Equals(categoryName, StringComparison.OrdinalIgnoreCase));
                if (cat == null && categories.Count > 0) cat = categories[0];
                cue.category = cat;

                _clipToCueCache[clip] = cue;
                RegisterCue(cue);
            }

            return PlaySE3D(cue, position, volumeScale);
        }

        /// <summary>
        /// BGMを再生します。現在再生中の他のBGMを自動的にクロスフェードアウトさせて停止します。
        /// </summary>
        public PureAudioVoice PlayBGM(PureAudioCue cue, float fadeTime = 1.0f, float volumeScale = 1f)
        {
            if (cue == null) return null;
            RecordCueTrigger(cue.cueName);

            foreach (var voice in _voicePool)
            {
                if (voice.State == VoiceState.Playing && voice.CurrentCue != null)
                {
                    if (voice.CurrentCue.category != null && 
                        voice.CurrentCue.category.categoryName.Equals("BGM", StringComparison.OrdinalIgnoreCase))
                    {
                        voice.Stop(fadeTime); 
                    }
                }
            }

            PureAudioVoice newVoice = GetAvailableVoice(cue);
            if (newVoice == null) return null;

            newVoice.Play(cue, null, Vector3.zero, volumeScale, attackTimeOverride: fadeTime, speaker: null, useUnscaledTime: false);
            AddLog("PLAY", cue.cueName, $"Started BGM on Voice {newVoice.VoiceId} with crossfade of {fadeTime}s.");
            return newVoice;
        }

        /// <summary>
        /// BGMを名前指定で再生します。
        /// </summary>
        public PureAudioVoice PlayBGM(string cueName, float fadeTime = 1.0f, float volumeScale = 1f)
        {
            if (_cueCache.TryGetValue(cueName, out var cue))
            {
                return PlayBGM(cue, fadeTime, volumeScale);
            }
            AddLog("WARN", cueName, $"PlayBGM failed: Cue not found.");
            return null;
        }

        /// <summary>
        /// ボイス・セリフを再生します（話者制限システム）。
        /// 指定された話者 (speaker) がすでに喋っているボイスがあれば自動でフェードアウト停止します。
        /// </summary>
        public PureAudioVoice PlayVoice(PureAudioCue cue, Transform speaker, float volumeScale = 1f)
        {
            if (cue == null) return null;
            RecordCueTrigger(cue.cueName);

            // 同一スピーカーが既に再生しているVoiceを停止
            if (speaker != null)
            {
                foreach (var voice in _voicePool)
                {
                    if (voice.State == VoiceState.Playing && voice.Speaker == speaker)
                    {
                        AddLog("STOP", voice.CurrentCue?.cueName, $"Voice overlap prevented: Stopped Voice {voice.VoiceId} for Speaker '{speaker.name}'.");
                        voice.Stop(0.15f); // 僅かにフェードさせて停止
                    }
                }
            }

            PureAudioVoice newVoice = GetAvailableVoice(cue);
            if (newVoice == null) return null;

            newVoice.Play(cue, speaker, Vector3.zero, volumeScale, attackTimeOverride: -1f, speaker: speaker, useUnscaledTime: false);
            AddLog("PLAY", cue.cueName, $"Started Voice on Voice {newVoice.VoiceId} for Speaker '{speaker?.name}'.");
            return newVoice;
        }

        /// <summary>
        /// ボイス・セリフを名前指定で再生します。
        /// </summary>
        public PureAudioVoice PlayVoice(string cueName, Transform speaker, float volumeScale = 1f)
        {
            if (_cueCache.TryGetValue(cueName, out var cue))
            {
                return PlayVoice(cue, speaker, volumeScale);
            }
            AddLog("WARN", cueName, $"PlayVoice failed: Cue not found.");
            return null;
        }

        /// <summary>
        /// ジングル（ファンファーレなどの割り込み音）を再生し、BGMカテゴリを自動的にダッキングさせます。
        /// ジングル終了後、BGMの音量は自動で滑らかに復元されます。
        /// </summary>
        public PureAudioVoice PlayJingle(PureAudioCue cue, float bgmDuckingVolume = 0.2f, float fadeTime = 0.3f, float volumeScale = 1f)
        {
            if (cue == null) return null;
            RecordCueTrigger(cue.cueName);

            PureAudioVoice voice = GetAvailableVoice(cue);
            if (voice == null) return null;

            voice.Play(cue, null, Vector3.zero, volumeScale, attackTimeOverride: -1f, speaker: null, useUnscaledTime: false);
            AddLog("PLAY", cue.cueName, $"Started Jingle on Voice {voice.VoiceId}. BGM Ducking triggered.");

            // BGMダッキングコルーチンの開始
            if (_duckingCoroutine != null) StopCoroutine(_duckingCoroutine);
            _duckingCoroutine = StartCoroutine(DuckingBgmRoutine(voice, bgmDuckingVolume, fadeTime));

            return voice;
        }

        /// <summary>
        /// ジングルを名前指定で再生し、BGMをダッキングさせます。
        /// </summary>
        public PureAudioVoice PlayJingle(string cueName, float bgmDuckingVolume = 0.2f, float fadeTime = 0.3f, float volumeScale = 1f)
        {
            if (_cueCache.TryGetValue(cueName, out var cue))
            {
                return PlayJingle(cue, bgmDuckingVolume, fadeTime, volumeScale);
            }
            AddLog("WARN", cueName, $"PlayJingle failed: Cue not found.");
            return null;
        }

        private IEnumerator DuckingBgmRoutine(PureAudioVoice jingleVoice, float targetDuckingVolume, float fadeTime)
        {
            // BGMカテゴリを取得
            PureAudioCategory bgmCategory = categories.Find(c => c != null && c.categoryName.Equals("BGM", StringComparison.OrdinalIgnoreCase));
            if (bgmCategory == null) yield break;

            _originalBgmVolume = bgmCategory.volume;
            float startVol = bgmCategory.RuntimeVolume;
            float targetVol = _originalBgmVolume * targetDuckingVolume;

            // 1. ダッキング（フェードアウト）
            float timer = 0f;
            while (timer < fadeTime)
            {
                timer += Time.deltaTime;
                bgmCategory.RuntimeVolume = Mathf.Lerp(startVol, targetVol, timer / fadeTime);
                yield return null;
            }
            bgmCategory.RuntimeVolume = targetVol;

            // 2. ジングルの再生完了（またはリリース状態移行）を待機
            yield return new WaitUntil(() => jingleVoice.State == VoiceState.Free || jingleVoice.EnvState == EnvelopeState.Release);

            // 3. 復帰（フェードイン）
            timer = 0f;
            startVol = bgmCategory.RuntimeVolume;
            while (timer < fadeTime)
            {
                timer += Time.deltaTime;
                bgmCategory.RuntimeVolume = Mathf.Lerp(startVol, _originalBgmVolume, timer / fadeTime);
                yield return null;
            }
            bgmCategory.RuntimeVolume = _originalBgmVolume;
            _duckingCoroutine = null;
        }

        /// <summary>
        /// 環境音 (Ambience) をループ再生します。
        /// </summary>
        public PureAudioVoice PlayAmb(PureAudioCue cue, float fadeTime = 2.0f, float volumeScale = 1f)
        {
            if (cue == null) return null;
            RecordCueTrigger(cue.cueName);

            PureAudioVoice voice = GetAvailableVoice(cue);
            if (voice == null) return null;

            // 環境音は原則ループ
            cue.isLooping = true; 

            voice.Play(cue, null, Vector3.zero, volumeScale, attackTimeOverride: fadeTime, speaker: null, useUnscaledTime: false);
            AddLog("PLAY", cue.cueName, $"Started Ambience on Voice {voice.VoiceId} with fade-in of {fadeTime}s.");
            return voice;
        }

        /// <summary>
        /// UIクリック音や効果音を再生します。ゲームポーズ中 (Time.timeScale = 0) でも unscaled 動作します。
        /// </summary>
        public PureAudioVoice PlayUI(PureAudioCue cue, float volumeScale = 1f)
        {
            if (cue == null) return null;
            RecordCueTrigger(cue.cueName);

            PureAudioVoice voice = GetAvailableVoice(cue);
            if (voice == null) return null;

            // useUnscaledTime を true にして再生
            voice.Play(cue, null, Vector3.zero, volumeScale, attackTimeOverride: -1f, speaker: null, useUnscaledTime: true);
            AddLog("PLAY", cue.cueName, $"Started UI sound on Voice {voice.VoiceId} (Unscaled Time).");
            return voice;
        }

        /// <summary>
        /// UIクリック音を名前指定で unscaled 再生します。
        /// </summary>
        public PureAudioVoice PlayUI(string cueName, float volumeScale = 1f)
        {
            if (_cueCache.TryGetValue(cueName, out var cue))
            {
                return PlayUI(cue, volumeScale);
            }
            AddLog("WARN", cueName, $"PlayUI failed: Cue not found.");
            return null;
        }

        #region Backwards Compatibility Overloads (Play / Play3D)
        public PureAudioVoice Play(PureAudioCue cue, Transform followTarget = null, float volumeScale = 1f) => PlaySE(cue, followTarget, volumeScale);
        public PureAudioVoice Play(string cueName, Transform followTarget = null, float volumeScale = 1f) => PlaySE(cueName, followTarget, volumeScale);
        public PureAudioVoice Play(AudioClip clip, Transform followTarget = null, float volumeScale = 1f, string categoryName = "SE") => PlaySE(clip, followTarget, volumeScale, categoryName);
        public PureAudioVoice Play3D(PureAudioCue cue, Vector3 position, float volumeScale = 1f) => PlaySE3D(cue, position, volumeScale);
        public PureAudioVoice Play3D(string cueName, Vector3 position, float volumeScale = 1f) => PlaySE3D(cueName, position, volumeScale);
        public PureAudioVoice Play3D(AudioClip clip, Vector3 position, float volumeScale = 1f, string categoryName = "SE") => PlaySE3D(clip, position, volumeScale, categoryName);
        #endregion

        /// <summary>
        /// すべてのアクティブな音声をフェードアウト停止します。
        /// </summary>
        public void StopAll(float fadeTime = 0.2f)
        {
            foreach (var voice in _voicePool)
            {
                if (voice.State != VoiceState.Free)
                {
                    voice.Stop(fadeTime);
                }
            }
        }

        /// <summary>
        /// 指定カテゴリに属するすべての音声をフェードアウト停止します。
        /// </summary>
        public void StopCategory(PureAudioCategory category, float fadeTime = 0.2f)
        {
            if (category == null) return;

            foreach (var voice in _voicePool)
            {
                if (voice.State != VoiceState.Free && voice.CurrentCue != null)
                {
                    if (voice.CurrentCue.category == category || IsSubcategoryOf(voice.CurrentCue.category, category))
                    {
                        voice.Stop(fadeTime);
                    }
                }
            }
        }

        /// <summary>
        /// すべてのアクティブな音声を一時停止します (UIなどのUnscaledTimeボイスは対象外)。
        /// </summary>
        public void PauseAll()
        {
            foreach (var voice in _voicePool)
            {
                if (voice.State == VoiceState.Playing && !voice.UseUnscaledTime)
                {
                    voice.SetPause(true);
                }
            }
            AddLog("SYSTEM", "All", "All scalable sounds paused.");
        }

        /// <summary>
        /// 一時停止中のすべての音声を再開します。
        /// </summary>
        public void ResumeAll()
        {
            foreach (var voice in _voicePool)
            {
                if (voice.State == VoiceState.Paused)
                {
                    voice.SetPause(false);
                }
            }
            AddLog("SYSTEM", "All", "All sounds resumed.");
        }

        /// <summary>
        /// 指定カテゴリに属するすべての音声を一時停止します。
        /// </summary>
        public void PauseCategory(PureAudioCategory category)
        {
            if (category == null) return;
            category.isPaused = true;

            foreach (var voice in _voicePool)
            {
                if (voice.State == VoiceState.Playing && voice.CurrentCue != null)
                {
                    if (voice.CurrentCue.category == category || IsSubcategoryOf(voice.CurrentCue.category, category))
                    {
                        voice.SetPause(true);
                    }
                }
            }
            AddLog("SYSTEM", category.categoryName, $"Category '{category.categoryName}' paused.");
        }

        /// <summary>
        /// 指定カテゴリに属するすべての一時停止中音声を再開します。
        /// </summary>
        public void ResumeCategory(PureAudioCategory category)
        {
            if (category == null) return;
            category.isPaused = false;

            foreach (var voice in _voicePool)
            {
                if (voice.State == VoiceState.Paused && voice.CurrentCue != null)
                {
                    if (voice.CurrentCue.category == category || IsSubcategoryOf(voice.CurrentCue.category, category))
                    {
                        voice.SetPause(false);
                    }
                }
            }
            AddLog("SYSTEM", category.categoryName, $"Category '{category.categoryName}' resumed.");
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

        #endregion

        #region Voice Limit & Stealing Logic (Advanced)

        private PureAudioVoice GetAvailableVoice(PureAudioCue cue)
        {
            // 1. キュー単位の発音制限チェック
            if (cue.maxVoices > 0)
            {
                int currentCueVoices = GetPlayingVoiceCount(v => v.CurrentCue == cue);
                if (currentCueVoices >= cue.maxVoices)
                {
                    int triggerCount = GetCueTriggerCountInLastSeconds(cue.cueName, 3.0f);
                    if (cue.limitBehavior == LimitBehavior.Prevent)
                    {
                        AddLog("PREVENT", cue.cueName, $"Play prevented: Cue limit reached ({cue.maxVoices}).");
                        AddWarning("WARN_LIMIT", cue.cueName, 
                            $"[Play Prevented] Cue limit ({cue.maxVoices}) reached. Triggered {triggerCount} times in last 3.0s.");
                        return null; 
                    }
                    else
                    {
                        PureAudioVoice victim = FindStealVictim(v => v.CurrentCue == cue, cue.limitBehavior);
                        if (victim != null)
                        {
                            float duration = Time.time - victim.PlayStartTime;
                            string victimCueName = victim.CurrentCue != null ? victim.CurrentCue.cueName : "Unknown";
                            AddLog("STEAL", cue.cueName, $"Voice {victim.VoiceId} stolen (Cue limit reached). Previous clip: {victim.CurrentClip?.name}");
                            
                            if (duration < shortPlayDurationThreshold)
                            {
                                AddWarning("WARN_LIMIT", victimCueName, 
                                    $"[Short-life Steal] Voice {victim.VoiceId} stolen by Cue '{cue.cueName}' after only {duration:F2}s. (Cue Limit: {cue.maxVoices}, Triggered {triggerCount} times in last 3.0s)");
                            }
                            victim.Stop(0.05f); 
                        }
                        else
                        {
                            AddLog("PREVENT", cue.cueName, $"Play prevented: Cue limit reached and no victim found.");
                            AddWarning("WARN_LIMIT", cue.cueName, 
                                $"[Play Prevented] Cue limit reached and no stealable victim found. Triggered {triggerCount} times in last 3.0s.");
                            return null;
                        }
                    }
                }
            }

            // 2. カテゴリ単位の発音制限チェック
            if (cue.category != null && cue.category.maxVoices > 0)
            {
                if (cue.category.ActiveVoiceCount >= cue.category.maxVoices)
                {
                    PureAudioVoice victim = FindStealVictim(v => v.CurrentCue != null && (v.CurrentCue.category == cue.category || IsSubcategoryOf(v.CurrentCue.category, cue.category)), LimitBehavior.StopOldest);
                    if (victim != null && victim.CurrentCue.priority <= cue.priority)
                    {
                        float duration = Time.time - victim.PlayStartTime;
                        string victimCueName = victim.CurrentCue != null ? victim.CurrentCue.cueName : "Unknown";
                        AddLog("STEAL", cue.cueName, $"Voice {victim.VoiceId} stolen (Category '{cue.category.categoryName}' limit reached). Previous cue: {victim.CurrentCue.cueName}");
                        
                        if (duration < shortPlayDurationThreshold)
                        {
                            AddWarning("WARN_LIMIT", victimCueName, 
                                $"[Short-life Steal] Voice {victim.VoiceId} (Category: {cue.category.categoryName}) stolen by Cue '{cue.cueName}' after only {duration:F2}s. (Category Limit: {cue.category.maxVoices})");
                        }
                        victim.Stop(0.05f);
                    }
                    else
                    {
                        AddLog("PREVENT", cue.cueName, $"Play prevented: Category '{cue.category.categoryName}' limit reached ({cue.category.maxVoices}).");
                        AddWarning("WARN_LIMIT", cue.cueName, 
                            $"[Play Prevented] Category '{cue.category.categoryName}' limit ({cue.category.maxVoices}) reached. Cue priority '{cue.priority}' <= victim priority.");
                        return null; 
                    }
                }
            }

            // 3. システム全体プール上限チェック
            int activeTotal = GetPlayingVoiceCount(v => v.State != VoiceState.Free);
            if (activeTotal >= poolSize)
            {
                PureAudioVoice victim = FindStealVictim(v => v.State != VoiceState.Free, LimitBehavior.StopOldest);
                if (victim != null && (victim.CurrentCue == null || victim.CurrentCue.priority <= cue.priority))
                {
                    float duration = Time.time - victim.PlayStartTime;
                    string victimCueName = victim.CurrentCue != null ? victim.CurrentCue.cueName : "Unknown";
                    AddLog("STEAL", cue.cueName, $"Voice {victim.VoiceId} stolen (Pool limit reached). Previous cue: {victim.CurrentCue?.cueName}");
                    
                    if (duration < shortPlayDurationThreshold)
                    {
                        AddWarning("WARN_LIMIT", victimCueName, 
                            $"[Short-life Steal] Voice {victim.VoiceId} (Pool limit reached) stolen by Cue '{cue.cueName}' after only {duration:F2}s. (Pool Limit: {poolSize})");
                    }
                    victim.Stop(0f); 
                }
                else
                {
                    AddLog("PREVENT", cue.cueName, "Play prevented: Pool limit reached and no stealable lower-priority voice.");
                    AddWarning("WARN_LIMIT", cue.cueName, 
                        $"[Play Prevented] Pool limit ({poolSize}) reached. No lower priority voice to steal.");
                    return null;
                }
            }

            foreach (var voice in _voicePool)
            {
                if (voice.State == VoiceState.Free)
                {
                    return voice;
                }
            }

            return null;
        }

        private int GetPlayingVoiceCount(Func<PureAudioVoice, bool> predicate)
        {
            int count = 0;
            foreach (var voice in _voicePool)
            {
                if (voice.State != VoiceState.Free && predicate(voice))
                {
                    count++;
                }
            }
            return count;
        }

        private PureAudioVoice FindStealVictim(Func<PureAudioVoice, bool> predicate, LimitBehavior behavior)
        {
            PureAudioVoice bestVictim = null;

            if (behavior == LimitBehavior.StopOldest)
            {
                float oldestTime = float.MaxValue;
                int lowestPriority = int.MaxValue;

                foreach (var voice in _voicePool)
                {
                    if (voice.State != VoiceState.Free && predicate(voice) && voice.EnvState != EnvelopeState.Release)
                    {
                        int priority = voice.CurrentCue != null ? voice.CurrentCue.priority : 0;
                        if (priority < lowestPriority)
                        {
                            lowestPriority = priority;
                            oldestTime = voice.PlayStartTime;
                            bestVictim = voice;
                        }
                        else if (priority == lowestPriority && voice.PlayStartTime < oldestTime)
                        {
                            oldestTime = voice.PlayStartTime;
                            bestVictim = voice;
                        }
                    }
                }
            }
            else if (behavior == LimitBehavior.StopNewest)
            {
                float newestTime = -1f;
                int lowestPriority = int.MaxValue;

                foreach (var voice in _voicePool)
                {
                    if (voice.State != VoiceState.Free && predicate(voice) && voice.EnvState != EnvelopeState.Release)
                    {
                        int priority = voice.CurrentCue != null ? voice.CurrentCue.priority : 0;
                        if (priority < lowestPriority)
                        {
                            lowestPriority = priority;
                            newestTime = voice.PlayStartTime;
                            bestVictim = voice;
                        }
                        else if (priority == lowestPriority && voice.PlayStartTime > newestTime)
                        {
                            newestTime = voice.PlayStartTime;
                            bestVictim = voice;
                        }
                    }
                }
            }

            return bestVictim;
        }

        #endregion

        #region Parameter (Modulation) API

        public void SetParameter(string paramName, float value)
        {
            if (string.IsNullOrEmpty(paramName)) return;
            _globalParameters[paramName] = Mathf.Clamp01(value);
        }

        public float GetParameter(string paramName)
        {
            if (string.IsNullOrEmpty(paramName)) return 0f;
            return _globalParameters.TryGetValue(paramName, out float val) ? val : 0f;
        }

        /// <summary>
        /// プレイモード中にエディタで編集されたアセットのキャッシュ参照を最新のものに同期します。
        /// </summary>
        public void SyncPlayModeCue(PureAudioCue cue)
        {
            if (cue == null || string.IsNullOrEmpty(cue.cueName)) return;
            _cueCache[cue.cueName] = cue;
        }

        #endregion

        #region Profiler Helpers

        public List<PureAudioVoice> GetVoicePool() => _voicePool;

        public long GetEstimatedMemoryBytes()
        {
            long totalBytes = 0;
            HashSet<AudioClip> activeClips = new HashSet<AudioClip>();

            foreach (var voice in _voicePool)
            {
                if (voice.State != VoiceState.Free && voice.CurrentClip != null)
                {
                    activeClips.Add(voice.CurrentClip);
                }
            }

            foreach (var clip in activeClips)
            {
                if (clip != null)
                {
                    totalBytes += (long)clip.samples * clip.channels * 2;
                }
            }

            return totalBytes;
        }

        #endregion
    }
}
