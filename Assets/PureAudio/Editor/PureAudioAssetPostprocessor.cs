using UnityEditor;
using UnityEngine;

namespace PureAudio.Editor
{
    /// <summary>
    /// 設定CSVファイル (PureAudioConfig.csv) の変更を自動で検知し、アセットへの自動インポートを実行するインポーター。
    /// </summary>
    public class PureAudioAssetPostprocessor : AssetPostprocessor
    {
        private const string TargetCsvPath = "Assets/PureAudio/Authoring/PureAudioConfig.csv";

        private static void OnPostprocessAllAssets(
            string[] importedAssets, 
            string[] deletedAssets, 
            string[] movedAssets, 
            string[] movedFromAssetPaths)
        {
            foreach (string str in importedAssets)
            {
                // 対象のCSVがインポートされた（更新または新規追加された）場合
                if (str.Equals(TargetCsvPath, System.StringComparison.OrdinalIgnoreCase))
                {
                    Debug.Log($"[PureAudio] CSV change detected at '{TargetCsvPath}'. Starting automatic import...");
                    
                    // インポートの実行
                    PureAudioExcelSync.ImportCsvToAssets();
                    
                    // アセットの再保存とコンパイル更新
                    AssetDatabase.SaveAssets();
                    break;
                }
            }
        }
    }
}
