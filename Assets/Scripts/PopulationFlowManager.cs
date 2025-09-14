using System.Collections.Generic;
using UnityEngine;
public class PopulationFlowManager : MonoBehaviour
{
    #region 設定

    [Header("Flow Direction")]
    [Tooltip("歩行者の流れ方向（左側通行かどうか）")]
    public bool IsLeftSideTraffic = false;

    [Header("歩行者の生成範囲")]
    [Tooltip("歩行者の生成範囲のスタートライン")]
    [SerializeField] private LineObject frontierStart;
    [Tooltip("歩行者の生成範囲のゴールライン")]
    [SerializeField] private LineObject frontierGoal;
    
    /// <summary>
    /// 中間ライン配列（Start → mid[0] → mid[1] → ... → Goal の順序で通過）
    /// </summary>
    [Header("Intermediate Lines")]
    [Tooltip("中間ライン配列（Start→Goal順で配置）")]
    [SerializeField] private List<LineObject> intermediateLines = new List<LineObject>();

    [Header("Pedestrian Prefab")]
    [Tooltip("歩行者のPrefab")]
    [SerializeField] private GameObject pedestrianPrefab;

    [Header("Pedestrian Count")]
    [Tooltip("スタート→ゴール方向の歩行者数")]
    [SerializeField] public int S2GPedestrianCount = 10;
    [Tooltip("ゴール→スタート方向の歩行者数")]
    [SerializeField] public int G2SPedestrianCount = 10;

    [Header("Special Pedestrian Settings")]
    [Tooltip("停止者の生成確率（%）")]
    [Range(0, 100)]
    public int RatioStationary = 10;
    [Tooltip("逆走者の生成確率（%）")]
    [Range(0, 100)]
    public int RatioReversing = 10;

    // 歩行者リスト
    private List<PedestrianController> start2GoalPedestrians = new List<PedestrianController>();
    private List<PedestrianController> goal2StartPedestrians = new List<PedestrianController>();

    #endregion

    #region 歩行者管理


    public void Start()
    {
        Regenerate();
    }

    // 指定数までプールを拡張（不足分のみ生成）
    private void EnsurePoolSizes(int s2gTarget, int g2sTarget)
    {
        if (pedestrianPrefab == null) return;

        // S2G
        if (start2GoalPedestrians.Count < s2gTarget)
        {
            int need = s2gTarget - start2GoalPedestrians.Count;
            CreatePedestrianGroup(need, isS2G: true, start2GoalPedestrians);
        }

        // G2S
        if (goal2StartPedestrians.Count < g2sTarget)
        {
            int need = g2sTarget - goal2StartPedestrians.Count;
            CreatePedestrianGroup(need, isS2G: false, goal2StartPedestrians);
        }
    }

    // 歩行者グループを生成（countは追加生成数）
    private void CreatePedestrianGroup(int count, bool isS2G, List<PedestrianController> targetList)
    {
        string groupName = isS2G ? "S2G" : "G2S";
        int startIndex = targetList.Count; // 既存数を基準に連番を付与

        for (int i = 0; i < count; i++)
        {
            GameObject pedestrianObj = Instantiate(pedestrianPrefab, this.transform);
            pedestrianObj.name = $"{groupName}_Pedestrian_{startIndex + i:00}";
            PedestrianController controller = pedestrianObj.GetComponent<PedestrianController>();
            
            if (controller != null)
            {
                // 歩行者の設定
                SetupPedestrianController(controller, isS2G);
                            
                targetList.Add(controller);
                pedestrianObj.SetActive(false); // 初期状態は非アクティブ
                
                // Debug.Log($"Created {groupName} pedestrian {startIndex + i}: {pedestrianObj.name}");
            }
            else
            {
                Debug.LogError($"PedestrianController not found on prefab! Destroying {pedestrianObj.name}");
                Destroy(pedestrianObj);
            }
        }
    }

    // PedestrianControllerの共通設定
    private void SetupPedestrianController(PedestrianController controller, bool isS2G)
    {
        controller.populationFlowManager = this;
        controller.frontierStart = frontierStart;
        controller.frontierGoal = frontierGoal;
        controller.intermediateLines = intermediateLines;
        controller.isS2G = isS2G;
    }


    // エピソード開始時に呼び出されるメソッド
    public void InitializePedestrians()
    {
        // 必要数だけアクティブにし、余剰は非アクティブ
        ActivatePedestrians();
    }

    // 歩行者をアクティブにして配置する
    private void ActivatePedestrians()
    {
        ActivatePedestrianGroup(start2GoalPedestrians, S2GPedestrianCount);
        ActivatePedestrianGroup(goal2StartPedestrians, G2SPedestrianCount);
    }

    // 歩行者グループをアクティブ/非アクティブ化（必要数だけアクティブ）
    private void ActivatePedestrianGroup(List<PedestrianController> pedestrianList, int activeCount)
    {
        for (int i = 0; i < pedestrianList.Count; i++)
        {
            var pedestrian = pedestrianList[i];
            bool shouldActive = i < activeCount;

            if (shouldActive)
            {
                // 特殊歩行者フラグの設定
                SetSpecialPedestrianFlags(pedestrian);
                
                // 停止者→通常に切り替わる場合に備え、Animatorの状態を明示的に同期
                var anim = pedestrian.CachedAnimator;
                if (anim != null)
                {
                    anim.enabled = !pedestrian.isStationaryPedestrian;
                }

                pedestrian.gameObject.SetActive(true);
                pedestrian.InitializePosition(IsLeftSideTraffic);
            }
            else
            {
                // 特殊フラグをリセット
                pedestrian.isReversePedestrian = false;
                pedestrian.isStationaryPedestrian = false;

                // Animatorを再有効化
                Animator animator = pedestrian.CachedAnimator;
                if (animator != null)
                    animator.enabled = true;

                pedestrian.gameObject.SetActive(false);
            }
        }
    }

    // 特殊歩行者フラグを設定
    private void SetSpecialPedestrianFlags(PedestrianController pedestrian)
    {
        // フラグをリセット
        pedestrian.isReversePedestrian = false;
        pedestrian.isStationaryPedestrian = false;

        // 逆走者を独立して設定
        bool isReverse = Random.Range(0, 100) < RatioReversing;
        pedestrian.isReversePedestrian = isReverse;

        // 停止者は逆走者以外から設定
        if (!pedestrian.isReversePedestrian)
        {
            pedestrian.isStationaryPedestrian = Random.Range(0, 100) < RatioStationary;
        }
    }

    // 外部からエピソードの開始/リセットに使用するパブリックメソッド
    [ContextMenu("Regenerate")]
    public void Regenerate()
    {
        // 必要数までプールを拡張してからアクティベート
        EnsurePoolSizes(S2GPedestrianCount, G2SPedestrianCount);
        InitializePedestrians();
    }


    #endregion

    #region Gizmo

    private void OnDrawGizmos()
    {
        // 実行時はGizmo描画を停止（エディタ上でのみ表示）
        if (Application.isPlaying) return;

        // スタートラインとゴールラインが設定されていない場合は何もしない
        if (frontierStart == null || frontierGoal == null) return;

        // エディタでリアルタイム更新（再生中以外）
        frontierStart.CalculateEndpoints();
        frontierGoal.CalculateEndpoints();

        // 中間ラインもエディタで更新
        if (intermediateLines != null)
        {
            foreach (var line in intermediateLines)
            {
                if (line != null) line.CalculateEndpoints();
            }
        }

        // 全ラインの統合された経路を描画
        DrawUnifiedPathLines();
    }

    /// <summary>
    /// スタートライン、中間ライン、ゴールラインを通る統合された経路を描画
    /// 左端、右端、中央線をそれぞれ描画し、中央線には方向矢印を付ける
    /// </summary>
    private void DrawUnifiedPathLines()
    {
        // 全ライン（スタート + 中間 + ゴール）を順序通りに取得
        List<LineObject> allLines = new List<LineObject> { frontierStart };
        if (intermediateLines != null)
        {
            allLines.AddRange(intermediateLines);
        }
        allLines.Add(frontierGoal);

        // 無効なラインを除外
        allLines.RemoveAll(line => line == null);
        
        if (allLines.Count < 2) return; // 最低2つのラインが必要

        // 左端線を描画（青色）
        Gizmos.color = new Color(0f, 0.5f, 1f, 0.8f);
        for (int i = 0; i < allLines.Count - 1; i++)
        {
            Gizmos.DrawLine(allLines[i].LeftPoint, allLines[i + 1].LeftPoint);
        }

        // 右端線を描画（赤色）
        Gizmos.color = new Color(1f, 0.2f, 0.2f, 0.8f);
        for (int i = 0; i < allLines.Count - 1; i++)
        {
            Gizmos.DrawLine(allLines[i].RightPoint, allLines[i + 1].RightPoint);
        }

        // 中央線を描画（黄色）
        Gizmos.color = new Color(1f, 1f, 0f, 0.8f);
        for (int i = 0; i < allLines.Count - 1; i++)
        {
            Vector3 centerStart = allLines[i].GetCenter();
            Vector3 centerEnd = allLines[i + 1].GetCenter();
            Gizmos.DrawLine(centerStart, centerEnd);
        }

        // 方向矢印を中央線に描画
        DrawDirectionArrows(allLines);
    }

    /// <summary>
    /// 中央線上に方向矢印を描画
    /// </summary>
    /// <param name="allLines">全ラインのリスト</param>
    private void DrawDirectionArrows(List<LineObject> allLines)
    {
        Gizmos.color = Color.yellow;
        
        for (int i = 0; i < allLines.Count - 1; i++)
        {
            Vector3 startCenter = allLines[i].GetCenter();
            Vector3 endCenter = allLines[i + 1].GetCenter();
            Vector3 direction = (endCenter - startCenter).normalized;
            
            // 矢印の位置（線分の中央付近）
            Vector3 arrowPosition = Vector3.Lerp(startCenter, endCenter, 0.7f);
            Vector3 arrowTip = arrowPosition + direction * 0.3f;
            
            // 矢印の線
            Gizmos.DrawLine(arrowPosition, arrowTip);
            
            // 矢印の先端
            Vector3 arrowSide1 = arrowTip - direction * 0.2f + Vector3.Cross(direction, Vector3.up) * 0.1f;
            Vector3 arrowSide2 = arrowTip - direction * 0.2f - Vector3.Cross(direction, Vector3.up) * 0.1f;
            Gizmos.DrawLine(arrowTip, arrowSide1);
            Gizmos.DrawLine(arrowTip, arrowSide2);
        }
    }

    #endregion
}
