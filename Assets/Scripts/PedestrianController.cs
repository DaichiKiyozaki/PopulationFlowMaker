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
    // 外部参照
    // - PopulationFlowManager: 左右通行設定などの共有コンフィグ
    // - frontierStart/frontierGoal: 経路の始端・終端ライン
    [HideInInspector] public PopulationFlowManager PopulationFlowManager;
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

    // 中間ライン制御
    private int currentTargetLineIndex = 0; // 次に目指すラインのインデックス（0～intermediateLines.Count）
    // - S2Gの場合: 0は最初の中間ライン（中間ライン無し時はゴールライン）、intermediateLines.Countはゴールライン
    // - G2Sの場合: 0は最初の目的ライン（ゴールライン）、intermediateLines.Countはスタートライン
    
    // 外部（Managerなど）から参照するためのキャッシュ済み Animator アクセサ
    public Animator CachedAnimator => cachedAnimator;

    private void Awake()
    {
        // 頻繁にアクセスするコンポーネント参照をキャッシュして、GetComponentの頻度を下げる
        navMeshAgent = GetComponent<NavMeshAgent>();
        cachedAnimator = GetComponent<Animator>();
    }

    // 指定座標に歩行者をワープさせる
    private void WarpOrSetPosition(Vector3 position)
    {
        if (navMeshAgent.isActiveAndEnabled)
            navMeshAgent.Warp(position);
        else
            transform.position = position;
    }

    // 歩行者の初期位置と方向を設定
    public void InitializePosition(bool isLeftSideTraffic)
    {
        if (frontierStart == null || frontierGoal == null) return;

        // 中間ライン追跡状態をリセット（最初のセグメントから再開）
        currentTargetLineIndex = 0;

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

    // 初期位置と目的地を計算
    private (Vector3 position, Vector3 destination) CalculateInitialPosition(bool isLeftSideTraffic)
    {
        var lines = GetAllLinesOrdered(isS2G);

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

        // セグメントの進行方向（中心点同士のベクトル）
        Vector3 forward = (dst.GetCenter() - src.GetCenter()).normalized;

        // 通行ルール（左右通行）に対応するオフセット幅を決定
        float halfLen = src.GetLength() * 0.5f;
        bool useLeft = ShouldUseLeftSide(isLeftSideTraffic);
        float offset = Random.Range(0f, halfLen);

        // src上の左右側点 p0 と、それに対応するdst上の点 p1 を取得
        Vector3 p0 = GetSidePositionOnLine(src, forward, useLeft, offset);
        Vector3 p1 = GetCorrespondingPointOnLine(p0, src, dst);

        // p0-p1間のランダム位置を初期位置とする
        Vector3 spawnPos = Vector3.Lerp(p0, p1, Random.value);

        // NavMesh上へスナップ
        Vector3 position = SnapToNavMeshXZ(spawnPos);

        // 現在のターゲットセグメントを保持（source=segIndex, target=segIndex+1）
        currentTargetLineIndex = segIndex;

        // 初期の目的地は p1（dst対応点）を使用
        return (position, p1);
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


    // 指定位置に歩行者をセットアップ（目的地設定・速度等の個体差を適用）
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

    // 目的地を超えたら位置をリセットして再度歩行開始（循環）
    private void ResetPosition()
    {
        if (frontierStart == null || frontierGoal == null || !navMeshAgent.isOnNavMesh) return;

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

        // 進行方向ベクトル
        Vector3 forward = (targetLine.GetCenter() - sourceLine.GetCenter()).normalized;

        // 片側幅の範囲でランダムオフセット
        float halfLength = sourceLine.GetLength() * 0.5f;
        bool useLeft = ShouldUseLeftSide(PopulationFlowManager.IsLeftSideTraffic);
        float offset = Random.Range(0f, halfLength);

        // 並進位置を算出し、NavMeshへスナップ
        Vector3 basePos = GetSidePositionOnLine(sourceLine, forward, useLeft, offset);
        return SnapToNavMeshXZ(basePos);
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

    // あるライン上の点に対応する別ライン上の点を取得
    private Vector3 GetCorrespondingPointOnLine(Vector3 sourcePoint, LineObject sourceLine, LineObject targetLine)
    {
        // sourceLine 左端→右端ベクトル
        Vector3 sourceLeft = sourceLine.LeftPoint;
        Vector3 sourceRight = sourceLine.RightPoint;
        Vector3 sourceVector = sourceRight - sourceLeft;

        // sourcePoint が sourceLine 上でどの割合(0-1)に位置するかを内積から求める
        float sourceRatio = 0f;
        float denom = sourceVector.sqrMagnitude;
        if (denom > Mathf.Epsilon)
        {
            sourceRatio = Mathf.Clamp01(Vector3.Dot(sourcePoint - sourceLeft, sourceVector) / denom);
        }

        // 求めた比率で targetLine の対応点を補間
        Vector3 targetLeft = targetLine.LeftPoint;
        Vector3 targetRight = targetLine.RightPoint;
        return Vector3.Lerp(targetLeft, targetRight, sourceRatio);
    }

    /// <summary>
    /// 現在のターゲットラインを取得
    /// </summary>
    private LineObject GetCurrentTargetLine()
    {
        var lines = GetAllLinesOrdered(isS2G);
        if (lines == null || lines.Count == 0) return null;

        // 範囲外アクセスを防ぐためにClamp
        int idx = Mathf.Clamp(currentTargetLineIndex + 1, 1, Mathf.Max(1, lines.Count - 1));
        return lines[idx];
    }

    /// <summary>
    /// 現在のソースラインを取得
    /// </summary>
    private LineObject GetCurrentSourceLine()
    {
        var lines = GetAllLinesOrdered(isS2G);
        if (lines == null || lines.Count == 0) return null;

        // 範囲外アクセスを防ぐためにClamp
        int idx = Mathf.Clamp(currentTargetLineIndex, 0, Mathf.Max(0, lines.Count - 2));
        return lines[idx];
    }

    // 目的地を設定：現在位置の対応点を次ターゲットライン上に求め、NavMeshAgentへ指示
    private void SetDestination()
    {
        if (frontierStart == null || frontierGoal == null) return;
        if (isStationaryPedestrian) return; // 停止者は目的地設定不要

        // 現在位置に対する対応点から次の目的地を決定
        Vector3 currentPosition = transform.position;
        LineObject sourceLine = GetCurrentSourceLine();
        LineObject targetLine = GetCurrentTargetLine();

        destination = GetCorrespondingPointOnLine(currentPosition, sourceLine, targetLine);
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

    // 進行方向に対して+90度回転したベクトルを「左」として扱い、中心から左右へオフセット   
    private Vector3 GetSidePositionOnLine(LineObject line, Vector3 forwardDir, bool useLeftSide, float offset)
    {
        // 進行方向に対して+90度回転したベクトルを「左」として扱い、中心から左右へオフセット
        Vector3 center = line.GetCenter();
        Vector3 leftDir = new Vector3(-forwardDir.z, 0f, forwardDir.x).normalized;
        Vector3 result;
        if (useLeftSide)
        {
            result = center + leftDir * offset;
        }
        else
        {
            result = center - leftDir * offset;
        }
        return result;
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
}
