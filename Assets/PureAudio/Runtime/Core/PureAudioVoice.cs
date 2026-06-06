using System;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace PureAudio
{
    public enum VoiceState
    {
        Free,       // プール内にあり未使用
        Loading,    // Addressablesで AudioClip をロード中
        Playing,    // 再生中 (ADSR処理中)
        Paused      // 一時停止中
    }

    public enum EnvelopeState
    {
        Idle,
        Attack,
        Decay,
        Sustain,
        Release
    }

    /// <summary>
    /// 単一の AudioSource を管理・制御するランタイムボイスクラス。
    /// ADSRエンベロープ（Attack, Decay, Sustain, Release）、動的フェードアウト時間指定、
    /// 話者追跡、Unscaled Timeによるポーズ中再生、3Dターゲット追従、Modulationフィルタなどを制御します。
    /// </summary>
    [RequireComponent(typeof(AudioSource))]
    public class PureAudioVoice : MonoBehaviour
    {
        public VoiceState State { get; private set; } = VoiceState.Free;
        public EnvelopeState EnvState { get; private set; } = EnvelopeState.Idle;
        public int VoiceId { get; internal set; }

        public PureAudioCue CurrentCue { get; private set; }
        public AudioClip CurrentClip { get; private set; }

        /// <summary>
        /// 現在このボイスを再生している話者 (Transform)。PlayVoiceでの多重発音防止用。
        /// </summary>
        public Transform Speaker { get; private set; }

        /// <summary>
        /// Time.timeScale = 0 (一時停止) 中でも再生を維持するかどうか。
        /// </summary>
        public bool UseUnscaledTime { get; private set; }

        private AudioSource _audioSource;
        private AudioLowPassFilter _lowPassFilter;
        private AudioHighPassFilter _highPassFilter;

        // 音量・ピッチ関係
        private float _volumeScale = 1f;
        private float _randomVolumeOffset = 0f;
        private float _randomPitchOffset = 0f;

        // ADSR状態追跡用
        private float _adsrVolume = 1f;
        private float _envelopeTimer = 0f;
        private float _releaseStartVolume = 1f;
        private float _releaseDuration = 0.1f;

        // ディレイ再生用
        private float _delayTimer = 0f;
        private bool _isDelaying = false;

        // 3D追従用
        private Transform _followTarget;
        private Vector3 _fixedPosition;
        private bool _isFollowing = false;

        // Addressablesロード管理用
        private AsyncOperationHandle<AudioClip> _loadHandle;
        private bool _isAddressableLoaded = false;

        // 再生開始時エポック時間（プロファイラ用）
        public float PlayStartTime { get; private set; } = 0f;

        private float _attackTimeOverride = -1f;
        private bool _volumeWarningLogged = false;

        private void Awake()
        {
            _audioSource = GetComponent<AudioSource>();
            _audioSource.playOnAwake = false;
            _audioSource.spatialBlend = 0f;
        }

        /// <summary>
        /// ボイスの状態を初期化（プール回収時用）
        /// </summary>
        public void ResetVoice()
        {
            StopImmediateInternal();
            State = VoiceState.Free;
            EnvState = EnvelopeState.Idle;
            CurrentCue = null;
            CurrentClip = null;
            Speaker = null;
            UseUnscaledTime = false;
            _followTarget = null;
            _isFollowing = false;
            _isDelaying = false;
            _delayTimer = 0f;
            _adsrVolume = 0f;
            _envelopeTimer = 0f;
            _volumeWarningLogged = false;
        }

        /// <summary>
        /// 再生処理を開始します（Addressableロードまたは直接参照再生）
        /// </summary>
        public void Play(
            PureAudioCue cue, 
            Transform followTarget, 
            Vector3 fixedPosition, 
            float volumeScale = 1f, 
            float attackTimeOverride = -1f, 
            Transform speaker = null, 
            bool useUnscaledTime = false)
        {
            ResetVoice();
            CurrentCue = cue;
            _volumeScale = volumeScale;
            _followTarget = followTarget;
            _fixedPosition = fixedPosition;
            _isFollowing = (followTarget != null);
            _attackTimeOverride = attackTimeOverride;
            Speaker = speaker;
            UseUnscaledTime = useUnscaledTime;

            // 3D位置の初期化
            if (_isFollowing)
            {
                transform.position = _followTarget.position;
            }
            else
            {
                transform.position = _fixedPosition;
            }

            // ランダムパラメータの決定
            _randomVolumeOffset = UnityEngine.Random.Range(-cue.volumeRandomRange, cue.volumeRandomRange);
            _randomPitchOffset = UnityEngine.Random.Range(-cue.pitchRandomRange, cue.pitchRandomRange);

            // ディレイ再生の設定
            if (cue.delay > 0f)
            {
                _isDelaying = true;
                _delayTimer = cue.delay;
                State = VoiceState.Playing; // 外からは「再生中」に見せる
            }

            // 音源のロード開始
            int referenceCount = cue.audioClipReferences != null ? cue.audioClipReferences.Count : 0;
            int directCount = cue.directAudioClips != null ? cue.directAudioClips.Count : 0;

            if (referenceCount > 0)
            {
                // Addressablesからロード
                State = VoiceState.Loading;
                int clipIndex = cue.GetNextClipIndex(referenceCount);
                var assetRef = cue.audioClipReferences[clipIndex];

                _loadHandle = Addressables.LoadAssetAsync<AudioClip>(assetRef);
                _loadHandle.Completed += OnAudioClipLoaded;
            }
            else if (directCount > 0)
            {
                // 直接参照からロード不要で再生
                int clipIndex = cue.GetNextClipIndex(directCount);
                CurrentClip = cue.directAudioClips[clipIndex];
                
                if (!_isDelaying)
                {
                    StartPlayback();
                }
            }
            else
            {
                Debug.LogWarning($"[PureAudio] No AudioClip found in Cue: {cue.cueName}");
                ResetVoice();
            }
        }

        private void OnAudioClipLoaded(AsyncOperationHandle<AudioClip> handle)
        {
            if (State != VoiceState.Loading)
            {
                Addressables.Release(handle);
                return;
            }

            if (handle.Status == AsyncOperationStatus.Succeeded)
            {
                CurrentClip = handle.Result;
                _isAddressableLoaded = true;
                
                if (!_isDelaying)
                {
                    StartPlayback();
                }
                else
                {
                    State = VoiceState.Playing; // ロード完了、ディレイ待機
                }
            }
            else
            {
                Debug.LogError($"[PureAudio] Failed to load Addressable AudioClip for Cue: {CurrentCue.cueName}");
                ResetVoice();
            }
        }

        private void StartPlayback()
        {
            if (CurrentClip == null)
            {
                ResetVoice();
                return;
            }

            State = VoiceState.Playing;
            PlayStartTime = Time.time;

            // AudioSourceへのパラメータ設定
            _audioSource.clip = CurrentClip;
            _audioSource.loop = CurrentCue.isLooping && (CurrentCue.loopBeginTime == 0f && CurrentCue.loopEndTime == 0f);
            _audioSource.pitch = Mathf.Clamp(CurrentCue.pitch + _randomPitchOffset, 0f, 3f);
            _audioSource.spatialBlend = CurrentCue.spatialBlend;

            // 3Dパラメータの上書き
            if (CurrentCue.override3D)
            {
                _audioSource.minDistance = CurrentCue.minDistance;
                _audioSource.maxDistance = CurrentCue.maxDistance;
                _audioSource.rolloffMode = CurrentCue.rolloffMode;
            }

            // カテゴリにAudioMixerGroupがあれば自動的に出力先を変更
            if (CurrentCue.category != null && CurrentCue.category.unityMixerGroup != null)
            {
                _audioSource.outputAudioMixerGroup = CurrentCue.category.unityMixerGroup;
            }

            // ADSRエンベロープの開始
            _envelopeTimer = 0f;
            float attack = _attackTimeOverride >= 0f ? _attackTimeOverride : CurrentCue.attackTime;
            if (attack > 0f)
            {
                EnvState = EnvelopeState.Attack;
                _adsrVolume = 0f;
            }
            else
            {
                _adsrVolume = 1f;
                TransitionToDecayOrSustain();
            }

            // 最終ボリュームの適用
            UpdateVolume();

            // Unscaled タイム設定：ポーズ画面でも再生できるように
            if (UseUnscaledTime)
            {
                _audioSource.velocityUpdateMode = AudioVelocityUpdateMode.Dynamic;
            }

            _audioSource.Play();
        }

        /// <summary>
        /// ボイスを停止します。指定されたフェードアウト時間（秒）を優先して Release に遷移します。
        /// </summary>
        public void Stop(float fadeDuration)
        {
            if (State == VoiceState.Free) return;

            if (fadeDuration <= 0f)
            {
                ResetVoice();
            }
            else
            {
                // リリース状態へ遷移
                EnvState = EnvelopeState.Release;
                _releaseStartVolume = _adsrVolume;
                _releaseDuration = fadeDuration;
                _envelopeTimer = 0f;
            }
        }

        /// <summary>
        /// ボイスを停止します。Cueに定義されたRelease時間でフェードアウトします。
        /// </summary>
        public void Stop()
        {
            if (State == VoiceState.Free) return;
            Stop(CurrentCue != null ? CurrentCue.releaseTime : 0.1f);
        }

        /// <summary>
        /// ボイスの一時停止 / 再開を設定します。
        /// </summary>
        public void SetPause(bool pause)
        {
            if (State == VoiceState.Free || State == VoiceState.Loading) return;

            if (pause && State == VoiceState.Playing)
            {
                _audioSource.Pause();
                State = VoiceState.Paused;
            }
            else if (!pause && State == VoiceState.Paused)
            {
                _audioSource.UnPause();
                State = VoiceState.Playing;
            }
        }

        /// <summary>
        /// 毎フレームの更新（フェード、位置追従、ループ監視、Modulation適用）を行います。
        /// </summary>
        public void UpdateVoice(float deltaTime)
        {
            if (State == VoiceState.Free) return;

            // UnscaledTimeオプション時は deltaTime を Time.unscaledDeltaTime に上書き
            float dt = UseUnscaledTime ? Time.unscaledDeltaTime : deltaTime;

            // ディレイ待機中
            if (_isDelaying)
            {
                _delayTimer -= dt;
                if (_delayTimer <= 0f)
                {
                    _isDelaying = false;
                    if (State == VoiceState.Playing) 
                    {
                        StartPlayback();
                    }
                }
            }

            if (State == VoiceState.Playing)
            {
                // 3D位置追従
                if (_isFollowing)
                {
                    if (_followTarget != null)
                    {
                        transform.position = _followTarget.position;
                    }
                    else
                    {
                        _isFollowing = false;
                    }
                }

                // ADSRエンベロープボリュームの更新
                UpdateEnvelope(dt);

                // Modulationと各種フィルターの適用
                ApplyModulationParameters();

                // 音量とその他パラメータのリアルタイム更新
                UpdateVolume();
                UpdateSourceParameters();

                // イントロ付きカスタムループの制御
                UpdateCustomLoop();

                // 再生完了チェック
                if (!_isDelaying && EnvState != EnvelopeState.Release && !_audioSource.isPlaying && State != VoiceState.Paused)
                {
                    // UnscaledTimeかつタイムスケール0の時はisPlayingが一時停止扱いになるため自然終了させない
                    if (UseUnscaledTime && Time.timeScale == 0f)
                    {
                        // タイムスケールが0のときは再生中とみなして維持
                    }
                    else
                    {
                        ResetVoice();
                    }
                }
            }
        }

        private void UpdateEnvelope(float dt)
        {
            if (CurrentCue == null) return;

            _envelopeTimer += dt;

            switch (EnvState)
            {
                case EnvelopeState.Attack:
                    float attack = _attackTimeOverride >= 0f ? _attackTimeOverride : CurrentCue.attackTime;
                    if (attack > 0f)
                    {
                        float ratio = Mathf.Clamp01(_envelopeTimer / attack);
                        _adsrVolume = ratio;
                        if (ratio >= 1f)
                        {
                            TransitionToDecayOrSustain();
                        }
                    }
                    else
                    {
                        _adsrVolume = 1f;
                        TransitionToDecayOrSustain();
                    }
                    break;

                case EnvelopeState.Decay:
                    if (CurrentCue.decayTime > 0f)
                    {
                        float ratio = Mathf.Clamp01(_envelopeTimer / CurrentCue.decayTime);
                        _adsrVolume = Mathf.Lerp(1f, CurrentCue.sustainLevel, ratio);
                        if (ratio >= 1f)
                        {
                            EnvState = EnvelopeState.Sustain;
                            _adsrVolume = CurrentCue.sustainLevel;
                        }
                    }
                    else
                    {
                        EnvState = EnvelopeState.Sustain;
                        _adsrVolume = CurrentCue.sustainLevel;
                    }
                    break;

                case EnvelopeState.Sustain:
                    _adsrVolume = CurrentCue.sustainLevel;
                    break;

                case EnvelopeState.Release:
                    if (_releaseDuration > 0f)
                    {
                        float ratio = Mathf.Clamp01(_envelopeTimer / _releaseDuration);
                        _adsrVolume = Mathf.Lerp(_releaseStartVolume, 0f, ratio);
                        if (ratio >= 1f)
                        {
                            ResetVoice();
                        }
                    }
                    else
                    {
                        ResetVoice();
                    }
                    break;
            }
        }

        private void TransitionToDecayOrSustain()
        {
            _envelopeTimer = 0f;
            if (CurrentCue.decayTime > 0f && CurrentCue.sustainLevel < 1f)
            {
                EnvState = EnvelopeState.Decay;
            }
            else
            {
                EnvState = EnvelopeState.Sustain;
                _adsrVolume = CurrentCue.sustainLevel;
            }
        }

        private void UpdateVolume()
        {
            if (CurrentCue == null) return;

            float cueVol = Mathf.Clamp01(CurrentCue.volume + _randomVolumeOffset);
            float categoryVol = CurrentCue.category != null ? CurrentCue.category.GetFinalVolume() : 1f;
            float modulationVolMultiplier = GetModulationValue(ModulationTarget.Volume, 1f);

            float finalVol = cueVol * _volumeScale * categoryVol * _adsrVolume * modulationVolMultiplier;
            _audioSource.volume = finalVol;

            // 音量警告チェック (再生中かつ未警告時のみ)
            if (!_volumeWarningLogged && State == VoiceState.Playing && PureAudioEngine.Instance != null)
            {
                float threshold = PureAudioEngine.Instance.volumeWarningLevel;
                if (finalVol > threshold)
                {
                    _volumeWarningLogged = true;
                    float db = finalVol > 0f ? 20f * Mathf.Log10(finalVol) : -80.0f;
                    float threshDb = threshold > 0f ? 20f * Mathf.Log10(threshold) : -80.0f;
                    PureAudioEngine.AddWarning("WARN_VOLUME", CurrentCue.cueName, 
                        $"[Volume Exceeded] Played with calculated volume {finalVol:F2} ({db:F1} dB), exceeding warning threshold of {threshold:F2} ({threshDb:F1} dB).");
                }
            }
        }

        private void UpdateSourceParameters()
        {
            if (CurrentCue == null) return;

            // 1. 空間ブレンド率のリアルタイム適用
            _audioSource.spatialBlend = CurrentCue.spatialBlend;

            // 2. 3D減衰距離のリアルタイム適用
            if (CurrentCue.override3D || CurrentCue.spatialBlend > 0f)
            {
                _audioSource.minDistance = CurrentCue.minDistance;
                _audioSource.maxDistance = CurrentCue.maxDistance;
            }

            // 3. ループのリアルタイム適用（シームレス切り替え対応）
            if (CurrentCue.loopBeginTime == 0f && CurrentCue.loopEndTime == 0f)
            {
                _audioSource.loop = CurrentCue.isLooping;
            }
            else
            {
                _audioSource.loop = false;
            }
        }

        private void UpdateCustomLoop()
        {
            if (CurrentCue == null || !CurrentCue.isLooping) return;
            if (CurrentCue.loopBeginTime == 0f && CurrentCue.loopEndTime == 0f) return;

            if (_audioSource.isPlaying || (UseUnscaledTime && Time.timeScale == 0f))
            {
                float currentTime = _audioSource.time;
                float loopEnd = CurrentCue.loopEndTime > 0f ? CurrentCue.loopEndTime : CurrentClip.length;

                if (currentTime >= loopEnd - 0.05f)
                {
                    _audioSource.time = CurrentCue.loopBeginTime;
                }
            }
        }

        private void ApplyModulationParameters()
        {
            if (CurrentCue == null)
            {
                if (_lowPassFilter != null) _lowPassFilter.enabled = false;
                if (_highPassFilter != null) _highPassFilter.enabled = false;
                return;
            }

            if (CurrentCue.modulationSettings.Count == 0)
            {
                if (_lowPassFilter != null) _lowPassFilter.enabled = false;
                if (_highPassFilter != null) _highPassFilter.enabled = false;
                _audioSource.pitch = Mathf.Clamp(CurrentCue.pitch + _randomPitchOffset, 0f, 3f);
                return;
            }

            float modulationPitchMultiplier = GetModulationValue(ModulationTarget.Pitch, 1f);
            _audioSource.pitch = Mathf.Clamp((CurrentCue.pitch + _randomPitchOffset) * modulationPitchMultiplier, 0f, 3f);

            float modulationLPFCutoff = GetModulationValue(ModulationTarget.LowPassCutoff, -1f);
            if (modulationLPFCutoff >= 0f)
            {
                if (_lowPassFilter == null) _lowPassFilter = gameObject.AddComponent<AudioLowPassFilter>();
                _lowPassFilter.enabled = true;
                _lowPassFilter.cutoffFrequency = Mathf.Clamp(modulationLPFCutoff, 10f, 22000f);
            }
            else if (_lowPassFilter != null)
            {
                _lowPassFilter.enabled = false;
            }

            float modulationHPFCutoff = GetModulationValue(ModulationTarget.HighPassCutoff, -1f);
            if (modulationHPFCutoff >= 0f)
            {
                if (_highHighPassFilterCheck() == null) _highPassFilter = gameObject.AddComponent<AudioHighPassFilter>();
                _highPassFilter.enabled = true;
                _highPassFilter.cutoffFrequency = Mathf.Clamp(modulationHPFCutoff, 10f, 22000f);
            }
            else if (_highPassFilter != null)
            {
                _highPassFilter.enabled = false;
            }
        }

        private AudioHighPassFilter _highHighPassFilterCheck()
        {
            if (_highPassFilter == null) _highPassFilter = GetComponent<AudioHighPassFilter>();
            return _highPassFilter;
        }

        private float GetModulationValue(ModulationTarget target, float defaultValue)
        {
            if (CurrentCue == null || CurrentCue.modulationSettings.Count == 0) return defaultValue;

            float finalVal = defaultValue;
            bool isFirst = true;

            foreach (var setting in CurrentCue.modulationSettings)
            {
                if (setting.target == target)
                {
                    float paramVal = PureAudioEngine.Instance.GetParameter(setting.parameterName);
                    float curveVal = setting.curve.Evaluate(paramVal);

                    if (isFirst)
                    {
                        finalVal = curveVal;
                        isFirst = false;
                    }
                    else
                    {
                        finalVal *= curveVal;
                    }
                }
            }

            return finalVal;
        }

        private void StopImmediateInternal()
        {
            if (_audioSource != null)
            {
                _audioSource.Stop();
                _audioSource.clip = null;
            }

            if (_isAddressableLoaded)
            {
                Addressables.Release(_loadHandle);
                _isAddressableLoaded = false;
            }

            _adsrVolume = 0f;
            _envelopeTimer = 0f;
        }

        private void OnDestroy()
        {
            StopImmediateInternal();
        }
    }
}
