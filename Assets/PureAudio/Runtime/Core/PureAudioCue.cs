using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AddressableAssets;

namespace PureAudio
{
    public enum PlayType
    {
        Single,
        Random,
        RandomNoRepeat,
        Sequential
    }

    public enum LimitBehavior
    {
        Prevent,      // 新しい発音を破棄
        StopOldest,   // 最も古いボイスを停止 (ボイススチール)
        StopNewest    // 再生中の最も新しいボイスを停止
    }

    public enum ModulationTarget
    {
        Volume,
        Pitch,
        LowPassCutoff,
        HighPassCutoff
    }

    [Serializable]
    public class ModulationSetting
    {
        [Tooltip("ゲーム内パラメータ名 (例: Speed, Health, RPM)")]
        public string parameterName;

        [Tooltip("パラメータの連動対象")]
        public ModulationTarget target;

        [Tooltip("パラメータ値 (0.0〜1.0) に応じた倍率や周波数カーブ。縦軸が効果倍率となります。")]
        public AnimationCurve curve = AnimationCurve.Linear(0f, 0f, 1f, 1f);
    }

    /// <summary>
    /// 高度な「キュー (Cue)」に相当するサウンドリソース定義アセット。
    /// 再生方式、音量、ピッチのランダマイズ、同時発音制限、3D設定、ループ、Modulation制御などすべてを内包します。
    /// </summary>
    [CreateAssetMenu(fileName = "NewCue", menuName = "PureAudio/Cue", order = 101)]
    public class PureAudioCue : ScriptableObject
    {
        [Header("Cue Properties")]
        [Tooltip("キューの一意な識別名。コードから再生する際に指定します。")]
        public string cueName;

        [Tooltip("所属するカテゴリ。")]
        public PureAudioCategory category;

        [Tooltip("再生方法（単一再生、ランダム、重複回避ランダム、シーケンシャル）。")]
        public PlayType playbackType = PlayType.Single;

        [Header("Audio Clips (Addressables & Direct)")]
        [Tooltip("Addressableによる非同期ロード用。WebGLデモなどで軽量化する際は推奨します。")]
        public List<AssetReferenceT<AudioClip>> audioClipReferences = new List<AssetReferenceT<AudioClip>>();

        [Tooltip("直接参照用のフォールバック。Addressablesを使用しない手軽なテスト用です。")]
        public List<AudioClip> directAudioClips = new List<AudioClip>();

        [Header("Volume & Pitch Settings")]
        [Range(0f, 1f)]
        public float volume = 1f;

        [Range(0f, 0.5f)]
        [Tooltip("再生ごとの音量のランダムブレ幅 (Volume ± RandomRange)")]
        public float volumeRandomRange = 0f;

        [Range(0.5f, 2.0f)]
        public float pitch = 1f;

        [Range(0f, 0.5f)]
        [Tooltip("再生ごとのピッチのランダムブレ幅 (Pitch ± RandomRange)")]
        public float pitchRandomRange = 0f;

        [Header("Loop Settings")]
        [Tooltip("ループ再生を有効にするか。")]
        public bool isLooping = false;

        [Tooltip("イントロ付きループ用のループ開始地点 (秒)")]
        public float loopBeginTime = 0f;

        [Tooltip("イントロ付きループ用のループ終了地点 (秒。0秒の場合はオーディオ全長の最後まで再生したのち開始点へ戻る)")]
        public float loopEndTime = 0f;

        [Header("Voice Limit Settings")]
        [Tooltip("このキューの最大同時発音数 (0は無制限)")]
        public int maxVoices = 0;

        [Tooltip("最大発音数に達した際のボイス挙動。")]
        public LimitBehavior limitBehavior = LimitBehavior.StopOldest;

        [Range(0, 255)]
        [Tooltip("システムやカテゴリ全体でボイス限界に達したときに優先的に残す度合い。数値が大きいほど消されにくい。")]
        public int priority = 128;

        [Header("Addressable Settings")]
        [Tooltip("このCueのオーディオアセットをAddressablesで非同期ロードするかどうか。")]
        public bool isAddressable = false;

        [Header("Delay & ADSR Envelope")]
        [Tooltip("再生要求から実際に発音するまでの遅延秒数。")]
        public float delay = 0f;

        [Tooltip("アタック時間 (秒): 音が立ち上がるまでの時間。")]
        public float attackTime = 0f;

        [Tooltip("ディケイ時間 (秒): 最大音量からSustain音量へ落ちるまでの時間。")]
        public float decayTime = 0f;

        [Range(0f, 1f)]
        [Tooltip("サステインレベル (比率): Decay完了後の音量比率（1.0の場合はDecay処理は行われません）。")]
        public float sustainLevel = 1f;

        [Tooltip("リリース時間 (秒): 音停止（Stop）が呼ばれてから音が消えるまでのフェードアウト時間。")]
        public float releaseTime = 0.1f;

        [Header("3D Spatial Settings")]
        [Range(0f, 1f)]
        [Tooltip("2D(0.0) 〜 3D(1.0) の空間ブレンド度合い。")]
        public float spatialBlend = 0f;

        [Tooltip("Unityの標準 AudioSource の3D設定を以下で強制的に上書きするかどうか。")]
        public bool override3D = false;

        [Tooltip("3D空間で減衰が開始される最小距離。")]
        public float minDistance = 1f;

        [Tooltip("3D空間で音が完全に消える最大距離。")]
        public float maxDistance = 30f;

        [Tooltip("距離による減衰カーブの種類。")]
        public AudioRolloffMode rolloffMode = AudioRolloffMode.Logarithmic;

        [Header("Modulation Settings")]
        [Tooltip("ゲーム内パラメータによる動的変調パラメータ設定リスト")]
        public List<ModulationSetting> modulationSettings = new List<ModulationSetting>();

        [Tooltip("ランダム再生時に、一度鳴らした音を次回以降の抽選から除外する回数（0で除外なしの完全ランダム）")]
        public int noRepeatHistoryCount = 0;

        // シーケンシャル・ランダム非重複用のランタイム追跡変数
        [NonSerialized] private int _sequentialIndex = 0;
        [NonSerialized] private int _lastPlayedIndex = -1;
        [NonSerialized] private Queue<int> _playedHistory = new Queue<int>();

        /// <summary>
        /// 再生タイプ（Random/Sequentialなど）に基づいて次に再生すべきAudioClipのインデックスを返す
        /// </summary>
        public int GetNextClipIndex(int clipCount)
        {
            if (clipCount <= 0) return -1;
            if (clipCount == 1) return 0;

            switch (playbackType)
            {
                case PlayType.Single:
                    return 0;

                case PlayType.Random:
                    // 履歴除外数のクランプ（最大 clipCount - 1 まで）
                    int historyLimit = Mathf.Clamp(noRepeatHistoryCount, 0, clipCount - 1);
                    if (_playedHistory == null) _playedHistory = new Queue<int>();

                    // 履歴数が多すぎる場合は切り詰める
                    while (_playedHistory.Count > historyLimit)
                    {
                        _playedHistory.Dequeue();
                    }

                    // 候補リストの作成
                    var candidates = new List<int>();
                    for (int i = 0; i < clipCount; i++)
                    {
                        if (!_playedHistory.Contains(i))
                        {
                            candidates.Add(i);
                        }
                    }

                    int selectedIndex = 0;
                    if (candidates.Count > 0)
                    {
                        selectedIndex = candidates[UnityEngine.Random.Range(0, candidates.Count)];
                    }
                    else
                    {
                        selectedIndex = UnityEngine.Random.Range(0, clipCount);
                    }

                    // 履歴に登録
                    if (historyLimit > 0)
                    {
                        _playedHistory.Enqueue(selectedIndex);
                        if (_playedHistory.Count > historyLimit)
                        {
                            _playedHistory.Dequeue();
                        }
                    }

                    return selectedIndex;

                case PlayType.RandomNoRepeat:
                    if (_playedHistory == null) _playedHistory = new Queue<int>();
                    int rndIdx;
                    do
                    {
                        rndIdx = UnityEngine.Random.Range(0, clipCount);
                    } while (rndIdx == _lastPlayedIndex && clipCount > 1);
                    _lastPlayedIndex = rndIdx;
                    return rndIdx;

                case PlayType.Sequential:
                    int seqIdx = _sequentialIndex % clipCount;
                    _sequentialIndex = (_sequentialIndex + 1) % clipCount;
                    return seqIdx;

                default:
                    return 0;
            }
        }

        private void OnEnable()
        {
            ResetRuntimeState();
        }

        /// <summary>
        /// ランタイムの再生履歴ステートを初期化
        /// </summary>
        public void ResetRuntimeState()
        {
            _sequentialIndex = 0;
            _lastPlayedIndex = -1;
            if (_playedHistory == null)
            {
                _playedHistory = new Queue<int>();
            }
            else
            {
                _playedHistory.Clear();
            }
        }

        private void OnValidate()
        {
            // パラメータのバリデーション
            if (noRepeatHistoryCount < 0) noRepeatHistoryCount = 0;
            if (volumeRandomRange < 0f) volumeRandomRange = 0f;
            if (pitchRandomRange < 0f) pitchRandomRange = 0f;
            if (loopBeginTime < 0f) loopBeginTime = 0f;
            if (loopEndTime < 0f) loopEndTime = 0f;
            if (loopEndTime > 0f && loopBeginTime >= loopEndTime) loopBeginTime = loopEndTime - 0.01f;
            if (minDistance < 0f) minDistance = 0f;
            if (maxDistance < minDistance) maxDistance = minDistance + 0.1f;

            if (attackTime < 0f) attackTime = 0f;
            if (decayTime < 0f) decayTime = 0f;
            if (releaseTime < 0f) releaseTime = 0f;
        }
    }
}
