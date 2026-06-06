using System;
using UnityEngine;
using UnityEngine.Audio;

namespace PureAudio
{
    /// <summary>
    /// サウンドのカテゴリ管理を行う ScriptableObject。
    /// 親子関係による階層化音量制御、UnityのAudioMixerGroupとの紐付け、同時発音数制限、ミュート、ポーズ状態を一括管理します。
    /// </summary>
    [CreateAssetMenu(fileName = "NewCategory", menuName = "PureAudio/Category", order = 100)]
    public class PureAudioCategory : ScriptableObject
    {
        [Header("Category Info")]
        [Tooltip("カテゴリ名。コードやExcelとの照合に使用します。")]
        public string categoryName;

        [Tooltip("親カテゴリ。親カテゴリの音量やミュート状態がこのカテゴリにも再帰的に乗算・反映されます。")]
        public PureAudioCategory parentCategory;

        [Header("Audio Mixer Integration")]
        [Tooltip("対応する Unity AudioMixerGroup。このカテゴリのボイスは自動的にこのグループに出力されます。")]
        public AudioMixerGroup unityMixerGroup;

        [Header("Volume & Mute Settings")]
        [Range(0f, 1f)]
        [Tooltip("基本音量 (0.0 〜 1.0)")]
        public float volume = 1f;

        [Tooltip("カテゴリ全体の同時発音数制限 (0は無制限)")]
        public int maxVoices = 0;

        [Tooltip("ミュート状態フラグ")]
        public bool isMuted = false;

        [Tooltip("一時停止状態フラグ")]
        public bool isPaused = false;

        // ランタイムで制御される状態
        [NonSerialized] private float _runtimeVolume = 1f;
        [NonSerialized] private int _activeVoiceCount = 0;

        /// <summary>
        /// ランタイム音量（デフォルトは基本音量と同じ、インゲームで変更可能）
        /// </summary>
        public float RuntimeVolume
        {
            get => _runtimeVolume;
            set => _runtimeVolume = Mathf.Clamp01(value);
        }

        /// <summary>
        /// 現在このカテゴリで再生中のアクティブなボイス数
        /// </summary>
        public int ActiveVoiceCount
        {
            get => _activeVoiceCount;
            internal set => _activeVoiceCount = Mathf.Max(0, value);
        }

        private void OnEnable()
        {
            ResetRuntimeState();
        }

        /// <summary>
        /// ランタイム時の動的パラメータを初期状態にリセット
        /// </summary>
        public void ResetRuntimeState()
        {
            _runtimeVolume = volume;
            _activeVoiceCount = 0;
        }

        /// <summary>
        /// 親階層まで遡ってミュート状態を再帰的にチェックします。
        /// </summary>
        public bool IsMutedRecursive()
        {
            if (isMuted) return true;
            if (parentCategory != null)
            {
                return parentCategory.IsMutedRecursive();
            }
            return false;
        }

        /// <summary>
        /// 親階層まで遡ってポーズ状態を再帰的にチェックします。
        /// </summary>
        public bool IsPausedRecursive()
        {
            if (isPaused) return true;
            if (parentCategory != null)
            {
                return parentCategory.IsPausedRecursive();
            }
            return false;
        }

        /// <summary>
        /// カテゴリがミュート、一時停止されているか、親カテゴリの音量比率などを加味した最終的な音量倍率を算出
        /// </summary>
        public float GetFinalVolume()
        {
            if (IsMutedRecursive()) return 0f;

            float finalVol = _runtimeVolume;
            if (parentCategory != null)
            {
                finalVol *= parentCategory.GetFinalVolume();
            }
            return finalVol;
        }
    }
}
