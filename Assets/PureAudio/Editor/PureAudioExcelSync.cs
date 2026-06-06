using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace PureAudio.Editor
{
    /// <summary>
    /// Excelで編集可能なCSVファイルとUnity内のScriptableObjectアセットを双方向同期させるエディタユーティリティ。
    /// ADSRエンベロープを含むすべてのパラメータのCSV出入力に対応。
    /// </summary>
    public static class PureAudioExcelSync
    {
        private const string DefaultCsvPath = "Assets/PureAudio/Authoring/PureAudioConfig.csv";
        private const string CueAssetFolder = "Assets/PureAudio/Resources/PureAudio/Cues";
        private const string CategoryAssetFolder = "Assets/PureAudio/Resources/PureAudio/Categories";

        // CSVヘッダーの定義 (ADSRおよびDelayを組み込み)
        private static readonly string[] CsvHeaders = {
            "CueName", "Category", "PlaybackType", "Volume", "VolumeRandom", 
            "Pitch", "PitchRandom", "MaxVoices", "LimitBehavior", "Priority", 
            "SpatialBlend", "IsLooping", "LoopBegin", "LoopEnd", 
            "Delay", "AttackTime", "DecayTime", "SustainLevel", "ReleaseTime",
            "MinDistance", "MaxDistance", "NoRepeatHistoryCount", "IsAddressable"
        };

        [MenuItem("Window/PureAudio/Sync/Import CSV ➔ Unity Assets")]
        public static void ImportCsvToAssets()
        {
            string fullPath = Path.Combine(Directory.GetCurrentDirectory(), DefaultCsvPath);
            if (!File.Exists(fullPath))
            {
                CreateDefaultCsvFile(fullPath);
                Debug.LogWarning($"[PureAudio] CSV file not found. Created a default template at '{DefaultCsvPath}'. Please edit it and try importing again.");
                return;
            }

            try
            {
                EnsureDirectoriesExist();
                int cueCount = 0;
                int categoryCount = 0;

                string[] lines = File.ReadAllLines(fullPath, Encoding.UTF8);
                if (lines.Length <= 1)
                {
                    Debug.LogWarning("[PureAudio] CSV file is empty or contains only headers.");
                    return;
                }

                // 1行目はヘッダー、2行目から解析
                for (int i = 1; i < lines.Length; i++)
                {
                    string line = lines[i].Trim();
                    if (string.IsNullOrEmpty(line)) continue;

                    string[] cols = ParseCsvLine(line);
                    if (cols.Length < CsvHeaders.Length)
                    {
                        Debug.LogWarning($"[PureAudio] CSV line {i + 1} has insufficient columns. Skipped.");
                        continue;
                    }

                    string cueName = cols[0].Trim();
                    string categoryName = cols[1].Trim();

                    if (string.IsNullOrEmpty(cueName)) continue;

                    // 1. カテゴリアセットの検索・自動生成
                    PureAudioCategory category = null;
                    if (!string.IsNullOrEmpty(categoryName))
                    {
                        category = GetOrCreateCategoryAsset(categoryName, ref categoryCount);
                    }

                    // 2. キューアセットの検索・自動生成・更新
                    PureAudioCue cue = GetOrCreateCueAsset(cueName, ref cueCount);
                    cue.cueName = cueName;
                    cue.category = category;

                    // 各種パラメータのパースと設定
                    Enum.TryParse(cols[2], out PlayType playbackType);
                    cue.playbackType = playbackType;

                    float.TryParse(cols[3], out float vol);
                    cue.volume = Mathf.Clamp01(vol);

                    float.TryParse(cols[4], out float volRand);
                    cue.volumeRandomRange = Mathf.Clamp(volRand, 0f, 0.5f);

                    float.TryParse(cols[5], out float pit);
                    cue.pitch = Mathf.Clamp(pit, 0.5f, 2f);

                    float.TryParse(cols[6], out float pitRand);
                    cue.pitchRandomRange = Mathf.Clamp(pitRand, 0f, 0.5f);

                    int.TryParse(cols[7], out int maxVoices);
                    cue.maxVoices = Mathf.Max(0, maxVoices);

                    Enum.TryParse(cols[8], out LimitBehavior limitBehavior);
                    cue.limitBehavior = limitBehavior;

                    int.TryParse(cols[9], out int priority);
                    cue.priority = Mathf.Clamp(priority, 0, 255);

                    float.TryParse(cols[10], out float spatial);
                    cue.spatialBlend = Mathf.Clamp01(spatial);

                    bool.TryParse(cols[11], out bool loop);
                    cue.isLooping = loop;

                    float.TryParse(cols[12], out float loopBegin);
                    cue.loopBeginTime = Mathf.Max(0f, loopBegin);

                    float.TryParse(cols[13], out float loopEnd);
                    cue.loopEndTime = Mathf.Max(0f, loopEnd);

                    // ADSR & Delay のインポート
                    float.TryParse(cols[14], out float delay);
                    cue.delay = Mathf.Max(0f, delay);

                    float.TryParse(cols[15], out float attack);
                    cue.attackTime = Mathf.Max(0f, attack);

                    float.TryParse(cols[16], out float decay);
                    cue.decayTime = Mathf.Max(0f, decay);

                    float.TryParse(cols[17], out float sustain);
                    cue.sustainLevel = Mathf.Clamp01(sustain);

                    float.TryParse(cols[18], out float release);
                    cue.releaseTime = Mathf.Max(0f, release);

                    // 減衰距離
                    float.TryParse(cols[19], out float minDst);
                    cue.minDistance = Mathf.Max(0f, minDst);

                    float.TryParse(cols[20], out float maxDst);
                    cue.maxDistance = Mathf.Max(cue.minDistance + 0.1f, maxDst);

                    int.TryParse(cols[21], out int historyCount);
                    cue.noRepeatHistoryCount = Mathf.Max(0, historyCount);

                    if (cols.Length > 22)
                    {
                        bool.TryParse(cols[22], out bool isAddressable);
                        cue.isAddressable = isAddressable;
                    }

                    EditorUtility.SetDirty(cue);
                }

                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                Debug.Log($"[PureAudio] CSV Import Completed. Imported {cueCount} Cues and {categoryCount} Categories successfully.");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[PureAudio] CSV Import Error: {ex.Message}");
            }
        }

        [MenuItem("Window/PureAudio/Sync/Export Unity Assets ➔ CSV")]
        public static void ExportAssetsToCsv()
        {
            string fullPath = Path.Combine(Directory.GetCurrentDirectory(), DefaultCsvPath);
            EnsureDirectoriesExist();

            try
            {
                string[] guids = AssetDatabase.FindAssets("t:PureAudioCue");
                List<PureAudioCue> cues = new List<PureAudioCue>();

                foreach (string guid in guids)
                {
                    string path = AssetDatabase.GUIDToAssetPath(guid);
                    var cue = AssetDatabase.LoadAssetAtPath<PureAudioCue>(path);
                    if (cue != null)
                    {
                        cues.Add(cue);
                    }
                }

                cues.Sort((a, b) => string.Compare(a.cueName, b.cueName, StringComparison.OrdinalIgnoreCase));

                StringBuilder sb = new StringBuilder();
                sb.AppendLine(string.Join(",", CsvHeaders));

                foreach (var cue in cues)
                {
                    string categoryName = cue.category != null ? cue.category.categoryName : "";
                    
                    string[] row = {
                        EscapeCsv(cue.cueName),
                        EscapeCsv(categoryName),
                        cue.playbackType.ToString(),
                        cue.volume.ToString("F2"),
                        cue.volumeRandomRange.ToString("F2"),
                        cue.pitch.ToString("F2"),
                        cue.pitchRandomRange.ToString("F2"),
                        cue.maxVoices.ToString(),
                        cue.limitBehavior.ToString(),
                        cue.priority.ToString(),
                        cue.spatialBlend.ToString("F2"),
                        cue.isLooping.ToString(),
                        cue.loopBeginTime.ToString("F2"),
                        cue.loopEndTime.ToString("F2"),
                        cue.delay.ToString("F2"),
                        cue.attackTime.ToString("F2"),
                        cue.decayTime.ToString("F2"),
                        cue.sustainLevel.ToString("F2"),
                        cue.releaseTime.ToString("F2"),
                        cue.minDistance.ToString("F2"),
                        cue.maxDistance.ToString("F2"),
                        cue.noRepeatHistoryCount.ToString(),
                        cue.isAddressable.ToString()
                    };

                    sb.AppendLine(string.Join(",", row));
                }

                File.WriteAllText(fullPath, sb.ToString(), Encoding.UTF8);
                AssetDatabase.Refresh();
                Debug.Log($"[PureAudio] Export Completed. Exported {cues.Count} Cues to '{DefaultCsvPath}'.");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[PureAudio] CSV Export Error: {ex.Message}");
            }
        }

        private static PureAudioCategory GetOrCreateCategoryAsset(string categoryName, ref int count)
        {
            string assetPath = $"{CategoryAssetFolder}/{categoryName}.asset";
            var category = AssetDatabase.LoadAssetAtPath<PureAudioCategory>(assetPath);

            if (category == null)
            {
                category = ScriptableObject.CreateInstance<PureAudioCategory>();
                category.categoryName = categoryName;
                AssetDatabase.CreateAsset(category, assetPath);
                count++;
            }
            return category;
        }

        private static PureAudioCue GetOrCreateCueAsset(string cueName, ref int count)
        {
            string assetPath = $"{CueAssetFolder}/{cueName}.asset";
            var cue = AssetDatabase.LoadAssetAtPath<PureAudioCue>(assetPath);

            if (cue == null)
            {
                cue = ScriptableObject.CreateInstance<PureAudioCue>();
                cue.cueName = cueName;
                AssetDatabase.CreateAsset(cue, assetPath);
                count++;
            }
            return cue;
        }

        private static void EnsureDirectoriesExist()
        {
            string authoringDir = Path.GetDirectoryName(DefaultCsvPath);
            if (!Directory.Exists(authoringDir)) Directory.CreateDirectory(authoringDir);

            if (!AssetDatabase.IsValidFolder(CueAssetFolder))
            {
                System.IO.Directory.CreateDirectory(CueAssetFolder);
            }
            if (!AssetDatabase.IsValidFolder(CategoryAssetFolder))
            {
                System.IO.Directory.CreateDirectory(CategoryAssetFolder);
            }
        }

        private static void CreateDefaultCsvFile(string fullPath)
        {
            string authoringDir = Path.GetDirectoryName(fullPath);
            if (!Directory.Exists(authoringDir)) Directory.CreateDirectory(authoringDir);

            StringBuilder sb = new StringBuilder();
            sb.AppendLine(string.Join(",", CsvHeaders));
            
            // ADSRパラメータ込みのサンプル行を書き出す
            sb.AppendLine("Weapon_Shoot,SE,RandomNoRepeat,0.80,0.05,1.00,0.10,3,StopOldest,128,1.00,False,0.00,0.00,0.00,0.00,0.00,1.00,0.05,1.00,20.00,0,False");
            sb.AppendLine("BGM_Main,BGM,Single,0.60,0.00,1.00,0.00,1,Prevent,255,0.00,True,10.50,45.00,0.00,1.50,0.00,1.00,1.50,0.00,30.00,0,False");
            sb.AppendLine("Footstep_Dirt,SE,Random,0.50,0.10,1.00,0.05,2,StopOldest,80,1.00,False,0.00,0.00,0.00,0.00,0.00,1.00,0.10,0.50,10.00,0,False");

            File.WriteAllText(fullPath, sb.ToString(), Encoding.UTF8);
            AssetDatabase.Refresh();
        }

        private static string[] ParseCsvLine(string line)
        {
            List<string> result = new List<string>();
            bool inQuotes = false;
            StringBuilder currentToken = new StringBuilder();

            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '"')
                {
                    inQuotes = !inQuotes;
                }
                else if (c == ',' && !inQuotes)
                {
                    result.Add(currentToken.ToString());
                    currentToken.Clear();
                }
                else
                {
                    currentToken.Append(c);
                }
            }
            result.Add(currentToken.ToString());
            return result.ToArray();
        }

        private static string EscapeCsv(string val)
        {
            if (string.IsNullOrEmpty(val)) return "";
            if (val.Contains(",") || val.Contains("\"") || val.Contains("\n") || val.Contains("\r"))
            {
                return "\"" + val.Replace("\"", "\"\"") + "\"";
            }
            return val;
        }
    }
}
