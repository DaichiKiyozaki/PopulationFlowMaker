using UnityEngine;
using UnityEngine.AI;
using System.Collections.Generic;
using Random = UnityEngine.Random;

// NavMeshAgent を使って、Start→中間→Goal のライン群に沿って
// 歩行者を制御するコンポーネント。
// 左右通行・逆走・停止者を考慮し、到達後は位置をリセットして循環させる。
[RequireComponent(typeof(NavMeshAgent))]
public class PedestrianController : MonoBehaviour
{
    #region Fields
    // 外部参照
    // - PopulationFlowManager: 左右通行設定などの共有コンフィグ
    // - frontierStart/frontierGoal: 経路の始端・終端ライン
    [HideInInspector] public PopulationFlowManager populationFlowManager;
    [HideInInspector] public LineObject frontierStart, frontierGoal;
    /// <summary>
    /// 中間ライン配列（Start → mid[0] → mid[1] → ... → Goal の順序）
    /// </summary>
    [HideInInspector] public List<LineObject> intermediateLines = new List<LineObject>();
    public bool isS2G = true; // スタート→ゴール方向かどうか

    // 特殊歩行者フラグ
    public bool isStationaryPedestrian = false; // 停止者フラグ
    public bool isReversePedestrian = false;    // 逆走者フラグ

    // 移動設定
    [Header("移動設定")]
    [SerializeField] private float minSpeed = 0.5f;
    [SerializeField] private float maxSpeed = 3.0f;

    // y座標の補正設定
    [SerializeField] private float navMeshSnapMaxDistance = 2.0f; // 近傍NavMeshへスナップする最大距離（YはNavMesh/Agentで自動管理）

    // 移動制御
    private NavMeshAgent navMeshAgent;
    private Vector3 destination;
    private Animator cachedAnimator;
    // サイクル（生成〜到達）中に維持する横方向の割合（0..1）
    private float lateralRatio = 0.5f;

    // 中間ライン制御
    private int currentTargetLineIndex = 0; // 次に目指すラインのインデックス（0～intermediateLines.Count）
    // - S2Gの場合: 0は最初の中間ライン（中間ライン無し時はゴールライン）、intermediateLines.Countはゴールライン
    // - G2Sの場合: 0は最初の目的ライン（ゴールライン）、intermediateLines.Countはスタートライン

    // 1サイクル（生成/リセット〜最終到達）間で再利用する、進行方向順のライン列
    private List<LineObject> orderedLines;

    // 外部（Managerなど）から参照するためのキャッシュ済み Animator アクセサ
    public Animator CachedAnimator => cachedAnimator;
    #endregion

    #region Unity Lifecycle
    private void Awake()
    {
        // 頻繁にアクセスするコンポーネント参照をキャッシュして、GetComponentの頻度を下げる
        navMeshAgent = GetComponent<NavMeshAgent>();
        cachedAnimator = GetComponent<Animator>();
    }
    #endregion

    #region Public API
    // 歩行者の初期位置と方向を設定
    public void InitializePosition(bool isLeftSideTraffic)
    {
        if (frontierStart == null || frontierGoal == null) return;

        // 中間ライン追跡状態をリセット（最初のセグメントから再開）
        currentTargetLineIndex = 0;

        // サイクル用ライン列を構築
        orderedLines = GetAllLinesOrdered(isS2G);

        // 初期位置と目的地を計算してセットアップ
        var positionData = CalculateInitialPosition(isLeftSideTraffic);
        SetupPedestrianAtPosition(positionData.position, positionData.destination);

        // Animator制御：可能なら有効化し、停止者はアニメ速度0、通常は1に設定
        var anim = cachedAnimator;
        if (anim != null)
        {
            anim.enabled = true;
            if (isStationaryPedestrian)
            {
                anim.speed = 0f;
            }
            else
            {
                anim.speed = 1f;
            }
        }
    }
    #endregion

    #region Unity Events
    // ライン到達時
    private void OnTriggerEnter(Collider other)
    {
        if (isStationaryPedestrian) return; // 停止者は衝突無視

        // 最終目的地到達チェック
        bool shouldReset = false;
        if (isS2G && frontierGoal != null && other.gameObject == frontierGoal.gameObject)
        {
            shouldReset = true;
        }
        else if (!isS2G && frontierStart != null && other.gameObject == frontierStart.gameObject)
        {
            shouldReset = true;
        }

        if (shouldReset)
        {
            // 到達後はセグメント進行を初期化して循環
            currentTargetLineIndex = 0;
            ResetPosition();
            return;
        }

        // 中間ライン到達チェック
        if (intermediateLines != null && intermediateLines.Count > 0)
        {
            LineObject currentTarget = GetCurrentTargetLine();
            if (currentTarget != null && other.gameObject == currentTarget.gameObject)
            {
                // 次のターゲットへ進め、目的地を更新
                currentTargetLineIndex++;
                SetDestination();
                return;
            }
        }
    }
    #endregion

    #region High-level Workflow
    // 指定位置に歩行者をセットアップ
    private void SetupPedestrianAtPosition(Vector3 position, Vector3 targetDestination)
    {
        destination = targetDestination;
        WarpOrSetPosition(position);
        ApplyRandomMovementParams();
        if (!isStationaryPedestrian)
        {
            navMeshAgent.SetDestination(destination);
            AlignWithDestination(); // 初期向きを目的地方向に合わせる
        }
    }

    // 目的地を超えたら位置をリセットして再度歩行開始（循環）
    private void ResetPosition()
    {
        if (frontierStart == null || frontierGoal == null || !navMeshAgent.isOnNavMesh) return;

        // サイクル用ライン列を再構築（初期生成時と同等に固定化）
        orderedLines = GetAllLinesOrdered(isS2G);

        // 再配置：現在のセグメントの左右半区間上にランダムに再配置
        Vector3 resetPosition = CalculateResetPosition();
        WarpOrSetPosition(resetPosition);

        // 目的地と移動パラメータを再設定
        SetDestination();
        ApplyRandomMovementParams();
        if (!isStationaryPedestrian)
        {
            AlignWithDestination();
        }
    }

    // 目的地を設定：現在位置の対応点を次ターゲットライン上に求め、NavMeshAgentへ指示
    private void SetDestination()
    {
        if (frontierStart == null || frontierGoal == null) return;
        if (isStationaryPedestrian) return; // 停止者は目的地設定不要

        // サイクルで保持している横割合で次ターゲットライン上の目的地を決定
        LineObject targetLine = GetCurrentTargetLine();
        if (targetLine == null) return;
        destination = Vector3.Lerp(targetLine.LeftPoint, targetLine.RightPoint, lateralRatio);
        navMeshAgent.SetDestination(destination);
    }

    // 進行方向に歩行者の向きを合わせる（Y軸回りのみ回転）
    private void AlignWithDestination()
    {
        Vector3 direction = destination - transform.position;
        direction.y = 0;

        if (direction != Vector3.zero)
        {
            transform.rotation = Quaternion.LookRotation(direction);
        }
    }
    #endregion

    #region Mid-level Calculations
    // 初期位置と目的地を計算
    private (Vector3 position, Vector3 destination) CalculateInitialPosition(bool isLeftSideTraffic)
    {
        // 初期化されていなければライン列を構築
        var lines = orderedLines ?? (orderedLines = GetAllLinesOrdered(isS2G));

        // 初期セグメントの選定：両端(Start/Goal)を避け、中間セグメントを優先
        int minSegIndex = 1;
        int maxSegIndex = lines.Count - 2;
        int segIndex;
        if (minSegIndex >= maxSegIndex)
        {
            Debug.LogWarning("ライン数が不足しています。スタート・ゴールを各１つ中間ライン２つ以上を設定してください。");
            segIndex = Random.Range(0, lines.Count - 1);
        }
        else
        {
            // minSegIndex 以上 maxSegIndex 未満の範囲でランダム選択
            segIndex = Random.Range(minSegIndex, maxSegIndex);
        }

        LineObject src = lines[segIndex];
        LineObject dst = lines[segIndex + 1];

        // Lerpベースに統一：進行方向に対する左右とラインの Left/Right の関係から横割合を決定
        bool useLeft = ShouldUseLeftSide(isLeftSideTraffic);
        lateralRatio = ChooseLateralRatioForSegment(src, dst, useLeft);

        // 決定した割合で source ライン上の対応点を取得（Lerp）
        Vector3 p0 = Vector3.Lerp(src.LeftPoint, src.RightPoint, lateralRatio);

        // 次ライン上では同じ割合の点を目的地にする
        Vector3 p1 = Vector3.Lerp(dst.LeftPoint, dst.RightPoint, lateralRatio);

        // p0-p1間のランダム位置を初期位置とする
        Vector3 spawnPos = Vector3.Lerp(p0, p1, Random.value);

        // NavMesh上へスナップ
        Vector3 position = SnapToNavMeshXZ(spawnPos);

        // 現在のターゲットセグメントを保持（source=segIndex, target=segIndex+1）
        currentTargetLineIndex = segIndex;

        // 初期の目的地は p1（dst対応点）を使用
        return (position, p1);
    }

    // リセット位置を計算
    // S2Gだったらスタートライン、G2Sだったらゴールラインの左右半区間上にランダムに再配置
    private Vector3 CalculateResetPosition()
    {
        LineObject sourceLine, targetLine;
        // Reset時のソース/ターゲットは経路の先頭側
        if (isS2G)
        {
            sourceLine = frontierStart;
            targetLine = (intermediateLines != null && intermediateLines.Count > 0)
                ? intermediateLines[0]
                : frontierGoal;
        }
        else
        {
            sourceLine = frontierGoal;
            targetLine = (intermediateLines != null && intermediateLines.Count > 0)
                ? intermediateLines[intermediateLines.Count - 1]
                : frontierStart;
        }

        // Lerpベースに統一：進行方向に対する左右とラインの Left/Right の関係から横割合を決定
        bool useLeft = ShouldUseLeftSide(populationFlowManager.IsLeftSideTraffic);
        lateralRatio = ChooseLateralRatioForSegment(sourceLine, targetLine, useLeft);

        // 決定した割合の位置へ再配置（Lerp）
        Vector3 basePos = Vector3.Lerp(sourceLine.LeftPoint, sourceLine.RightPoint, lateralRatio);
        return SnapToNavMeshXZ(basePos);
    }

    /// <summary>
    /// 全ラインを進行方向順（S2G/G2S）に整列して返す
    /// </summary>
    private List<LineObject> GetAllLinesOrdered(bool s2g)
    {
        var list = new List<LineObject> { frontierStart };
        if (intermediateLines != null)
        {
            list.AddRange(intermediateLines);
        }
        list.Add(frontierGoal);

        // G2S の場合は反転
        if (!s2g)
        {
            list.Reverse();
        }

        return list;
    }

    /// <summary>
    /// 現在のターゲットラインを取得
    /// </summary>
    private LineObject GetCurrentTargetLine()
    {
        var lines = orderedLines ?? (orderedLines = GetAllLinesOrdered(isS2G));
        if (lines == null || lines.Count == 0) return null;

        // 範囲外アクセスを防ぐためにClamp
        int idx = Mathf.Clamp(currentTargetLineIndex + 1, 1, Mathf.Max(1, lines.Count - 1));
        return lines[idx];
    }
    #endregion

    #region Utilities
    // 指定座標に歩行者をワープさせる
    private void WarpOrSetPosition(Vector3 position)
    {
        if (navMeshAgent.isActiveAndEnabled)
            navMeshAgent.Warp(position);
        else
            transform.position = position;
    }

    // 近傍のNavMesh上にXZを維持したままスナップ（見つからない時は高さのみ現状を保持して返す）
    private Vector3 SnapToNavMeshXZ(Vector3 approx)
    {
        NavMeshHit hit;
        if (NavMesh.SamplePosition(approx, out hit, navMeshSnapMaxDistance, NavMesh.AllAreas))
        {
            return hit.position;
        }
        // フォールバック：NavMeshが見つからない場合はXZのみ採用
        return new Vector3(approx.x, transform.position.y, approx.z);
    }

    // 通行ルールと逆走者フラグに基づき、左右通行側を決定
    private bool ShouldUseLeftSide(bool globalIsLeftSideTraffic)
    {
        // 逆走者は左右通行を反転させる
        if (isReversePedestrian)
        {
            return !globalIsLeftSideTraffic;
        }
        else
        {
            return globalIsLeftSideTraffic;
        }
    }

    /// <summary>
    /// 指定セグメント（source→target）の進行方向に対し、歩行レーンの左半/右半から
    /// lateral ratio（0..1）を選ぶ。LineObject.Left/Right と幾何学的な左/右は一致しない
    /// 可能性があるため、毎回「進行方向の左ベクトル」に対して LeftPoint が左側かを判定する。
    /// </summary>
    private float ChooseLateralRatioForSegment(LineObject source, LineObject target, bool useLeft)
    {
        // forward（XZ）。退化時は line の Left→Right を代用する。
        Vector3 forward = target.GetCenter() - source.GetCenter();
        forward.y = 0f;
        // ほぼゼロ区間の場合は source の Left→Right を代用
        if (forward.sqrMagnitude < 0.01f)
        {
            forward = source.RightPoint - source.LeftPoint;
            forward.y = 0f;
        }
        forward.Normalize();

        // 進行方向の「左」ベクトル（XZ）
        Vector3 left = new Vector3(-forward.z, 0f, forward.x);

        // source ラインの幾何学的な左側にある端点が LeftPoint かをチェック
        Vector3 center = source.GetCenter();
        bool leftPointIsGeometricLeft = Vector3.Dot(source.LeftPoint - center, left) >= 0f;

        // 要求サイドに応じて半区間を選択
        if (useLeft)
            return leftPointIsGeometricLeft ? Random.Range(0f, 0.5f) : Random.Range(0.5f, 1f);
        else
            return leftPointIsGeometricLeft ? Random.Range(0.5f, 1f) : Random.Range(0f, 0.5f);
    }

    // NavMeshパラメータをランダムに適用
    private void ApplyRandomMovementParams()
    {
        // 停止者は速度0/停止。移動者は速度と回避優先度にランダム性を付与
        if (isStationaryPedestrian)
        {
            navMeshAgent.speed = 0f;
            navMeshAgent.isStopped = true;
            return;
        }

        navMeshAgent.speed = Random.Range(minSpeed, maxSpeed);
        navMeshAgent.avoidancePriority = Random.Range(0, 100);
        navMeshAgent.isStopped = false;
    }
    #endregion
}
