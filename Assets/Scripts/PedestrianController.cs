using UnityEngine;
using UnityEngine.AI;
using System.Collections.Generic;
using Random = UnityEngine.Random;

[RequireComponent(typeof(NavMeshAgent))]
public class PedestrianController : MonoBehaviour
{
    // 外部参照
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
    private float navMeshSnapMaxDistance = 2.0f; // 近傍NavMeshへスナップする最大距離（YはNavMesh/Agentで自動管理）

    // 移動制御
    private NavMeshAgent navMeshAgent;
    private Vector3 destination;
    private Animator cachedAnimator;

    // 中間ライン制御
    private int currentTargetLineIndex = 0; // 次に目指すラインのインデックス（0～intermediateLines.Count）
    // - S2Gの場合: 0は最初の中間ライン（中間ライン無し時はゴールライン）、intermediateLines.Countはゴールライン
    // - G2Sの場合: 0は最初の目的ライン（ゴールライン）、intermediateLines.Countはスタートライン


    private void Awake()
    {
        navMeshAgent = GetComponent<NavMeshAgent>();
        cachedAnimator = GetComponent<Animator>();
    }

    // 歩行者の初期位置と方向を設定
    public void InitializePosition(bool isLeftSideTraffic)
    {
        if (frontierStart == null || frontierGoal == null) return;

        // 中間ライン追跡状態をリセット
        currentTargetLineIndex = 0;

        var positionData = CalculateInitialPosition(isLeftSideTraffic);
        SetupPedestrianAtPosition(positionData.position, positionData.destination);

        // Animator制御：停止者の浮きを避けるため、可能なら有効のまま速度0にする
        var anim = cachedAnimator;
        if (anim != null)
        {
            anim.enabled = true;
            anim.speed = isStationaryPedestrian ? 0f : 1f;
        }
    }

    // 外部（Managerなど）から参照するためのキャッシュ済み Animator アクセサ
    public Animator CachedAnimator => cachedAnimator;

    // 初期位置と目的地を計算
    private (Vector3 position, Vector3 destination) CalculateInitialPosition(bool isLeftSideTraffic)
    {
        var lines = GetAllLinesOrdered(isS2G);
        int minSegIndex = 1;
        int maxSegIndex = lines.Count - 2;
        int segIndex;
        if (minSegIndex >= maxSegIndex)
        {
            segIndex = Mathf.Clamp(Mathf.FloorToInt((lines.Count - 1) * 0.5f), 0, Mathf.Max(0, lines.Count - 2));
        }
        else
        {
            segIndex = Random.Range(minSegIndex, maxSegIndex); // maxSegIndex は排他
        }
        LineObject src = lines[segIndex];
        LineObject dst = lines[segIndex + 1];

        Vector3 forward = (dst.GetCenter() - src.GetCenter()).normalized;
        float halfLen = src.GetLength() * 0.5f;
        bool useLeft = ShouldUseLeftSide(isLeftSideTraffic);
        float offset = Random.Range(0f, halfLen);
        Vector3 p0 = GetSidePositionOnLine(src, forward, useLeft, offset);
        Vector3 p1 = GetCorrespondingPointOnLine(p0, src, dst);
        Vector3 spawnPos = Vector3.Lerp(p0, p1, Random.value);
        Vector3 position = SnapToNavMeshXZ(spawnPos);
        currentTargetLineIndex = segIndex;
        return (position, p1);
    }

    /// <summary>
    /// 全ラインを進行方向順に並べたリストを取得
    /// </summary>
    /// <remarks>初期化用: 外部からの呼び出しは想定しません</remarks>
    /// </summary>
    /// <param name="s2g">true: Start→Goal, false: Goal→Start</param>
    /// <returns>進行方向順のラインリスト</returns>
    private List<LineObject> GetAllLinesOrdered(bool s2g)
    {
        var list = new List<LineObject> { frontierStart };
        if (intermediateLines != null)
        {
            list.AddRange(intermediateLines);
        }
        list.Add(frontierGoal);

        if (!s2g)
        {
            list.Reverse(); // G2S なら逆順で保持
        }

        return list;
    }


    // リセット位置を計算（ソースラインと次の目的ラインを基準に算出）
    private Vector3 CalculateResetPosition()
    {
        LineObject sourceLine = GetCurrentSourceLine();
        LineObject targetLine = GetCurrentTargetLine();
        if (sourceLine == null || targetLine == null) return transform.position;
        Vector3 forward = (targetLine.GetCenter() - sourceLine.GetCenter()).normalized;
        float halfLength = sourceLine.GetLength() * 0.5f;
        bool useLeft = ShouldUseLeftSide(PopulationFlowManager.IsLeftSideTraffic);
        float offset = Random.Range(0f, halfLength);
        Vector3 basePos = GetSidePositionOnLine(sourceLine, forward, useLeft, offset);
        return SnapToNavMeshXZ(basePos);
    }

    // 指定位置に歩行者をセットアップ
    private void SetupPedestrianAtPosition(Vector3 position, Vector3 targetDestination)
    {
        destination = targetDestination;
        if (navMeshAgent.isActiveAndEnabled)
        {
            if (!navMeshAgent.Warp(position)) transform.position = position;
        }
        else
        {
            transform.position = position;
        }
        ApplyRandomMovementParams();
        if (!isStationaryPedestrian)
        {
            navMeshAgent.SetDestination(destination);
            AlignWithDestination();
        }
    }

    // 近傍のNavMesh上にXZを維持したままスナップ（YはNavMesh基準）
    private Vector3 SnapToNavMeshXZ(Vector3 approx)
    {
        NavMeshHit hit;
        if (NavMesh.SamplePosition(approx, out hit, navMeshSnapMaxDistance, NavMesh.AllAreas))
        {
            return hit.position;
        }
        // 見つからない場合は現在の高さを維持して返す
        return new Vector3(approx.x, transform.position.y, approx.z);
    }

    // ラインの適切な側からランダムな点を取得
    private Vector3 GetRandomPointOnLineSide(LineObject sourceLine, LineObject destinationLine, bool useLeftSide)
    {
        Vector3 centerPoint = sourceLine.GetCenter();
        float halfLength = sourceLine.GetLength() / 2f;

        // sourceLineからdestinationLineへの方向ベクトル（実際のセグメント方向）
        Vector3 forward = (destinationLine.GetCenter() - sourceLine.GetCenter()).normalized;

        // 進行方向から見た左方向を計算（90度左回転）
        Vector3 leftDirection = new Vector3(-forward.z, 0, forward.x).normalized;

        // 逆走者の場合は通行位置を逆にする
        bool effectiveUseLeftSide = isReversePedestrian ? !useLeftSide : useLeftSide;

        float offset = Random.Range(0, halfLength);

        // 左側通行者として位置を決定
        if (effectiveUseLeftSide)
        {
            return centerPoint + leftDirection * offset;
        }
        // 右側通行者として位置を決定
        else
        {
            return centerPoint - leftDirection * offset;
        }
    }

    // あるライン上の点に対応する別のライン上の点を取得
    private Vector3 GetCorrespondingPointOnLine(Vector3 sourcePoint, LineObject sourceLine, LineObject targetLine)
    {
        // ソースライン上での位置を0-1の比率として計算
        Vector3 sourceLeft = sourceLine.LeftPoint;
        Vector3 sourceRight = sourceLine.RightPoint;
        Vector3 sourceVector = sourceRight - sourceLeft;

        Vector3 toSourcePoint = sourcePoint - sourceLeft;
        // 内積(Dot)を使って、点がラインに沿ってどれくらい進んだかを計算
        float sourceRatio = Vector3.Dot(toSourcePoint, sourceVector.normalized) / sourceVector.magnitude;
        // 0-1の範囲に収める（基本は元々0-1に収まっているが、例外もあり得る）
        sourceRatio = Mathf.Clamp01(sourceRatio);

        // ターゲットライン上の対応する位置を計算
        Vector3 targetLeft = targetLine.LeftPoint;
        Vector3 targetRight = targetLine.RightPoint;

        return Vector3.Lerp(targetLeft, targetRight, sourceRatio);
    }

    /// <summary>
    /// 現在のターゲットラインを取得（中間ライン対応）
    /// </summary>
    /// <returns>現在目指すべきLineObject</returns>
    private LineObject GetCurrentTargetLine()
    {
        if (intermediateLines == null || intermediateLines.Count == 0)
        {
            // 中間ラインが無い場合
            return isS2G ? frontierGoal : frontierStart;
        }

        if (isS2G)
        {
            // S2G: Start → mid[0] → mid[1] → ... → Goal
            if (currentTargetLineIndex < intermediateLines.Count)
            {
                return intermediateLines[currentTargetLineIndex];
            }
            else
            {
                return frontierGoal; // 最終ゴール
            }
        }
        else
        {
            // G2S: Goal → mid[N-1] → ... → mid[0] → Start (逆順)
            int reversedIndex = intermediateLines.Count - 1 - currentTargetLineIndex;
            if (reversedIndex >= 0)
            {
                return intermediateLines[reversedIndex];
            }
            else
            {
                return frontierStart; // 最終ゴール
            }
        }
    }

    /// <summary>
    /// 現在位置から適切なソースラインを取得
    /// </summary>
    /// <returns>現在位置に対応するソースライン</returns>
    private LineObject GetCurrentSourceLine()
    {
        if (intermediateLines == null || intermediateLines.Count == 0)
        {
            // 中間ラインが無い場合は従来通り
            return isS2G ? frontierStart : frontierGoal;
        }

        if (isS2G)
        {
            // S2G: 最初はStart、その後は前の中間ライン
            if (currentTargetLineIndex == 0)
            {
                return frontierStart;
            }
            else if (currentTargetLineIndex <= intermediateLines.Count)
            {
                return intermediateLines[currentTargetLineIndex - 1];
            }
            else
            {
                return intermediateLines[intermediateLines.Count - 1];
            }
        }
        else
        {
            // G2S: 最初はGoal、その後は前の中間ライン（逆順）
            if (currentTargetLineIndex == 0)
            {
                return frontierGoal;
            }
            else
            {
                int reversedIndex = intermediateLines.Count - currentTargetLineIndex;
                if (reversedIndex >= 0 && reversedIndex < intermediateLines.Count)
                {
                    return intermediateLines[reversedIndex];
                }
                else
                {
                    return frontierStart;
                }
            }
        }
    }

    // 目的地を設定
    private void SetDestination()
    {
        if (frontierStart == null || frontierGoal == null) return;

        // 停止者の場合は目的地設定を無視
        if (isStationaryPedestrian) return;

        Vector3 currentPosition = transform.position;
        LineObject sourceLine = GetCurrentSourceLine();
        LineObject targetLine = GetCurrentTargetLine();

        destination = GetCorrespondingPointOnLine(currentPosition, sourceLine, targetLine);
        navMeshAgent.SetDestination(destination);
    }

    // 進行方向に歩行者の向きを合わせる
    private void AlignWithDestination()
    {
        Vector3 direction = destination - transform.position;
        direction.y = 0; // Y軸は無視して水平方向だけ向きを合わせる

        if (direction != Vector3.zero)
        {
            transform.rotation = Quaternion.LookRotation(direction);
        }
    }

    // ラインに衝突した時の処理（中間ラインor最終ライン）
    private void OnTriggerEnter(Collider other)
    {
        // 停止者の場合は衝突判定を無視
        if (isStationaryPedestrian) return;

        // まず最終到達点かどうかをチェック（優先）
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
            // 中間ライン追跡をリセット
            currentTargetLineIndex = 0;
            ResetPosition();
            return; // 最終到達時は他の処理をスキップ
        }

        // 中間ライン到達チェック（最終到達でない場合のみ）
        if (intermediateLines != null && intermediateLines.Count > 0)
        {
            LineObject currentTarget = GetCurrentTargetLine();
            if (currentTarget != null && other.gameObject == currentTarget.gameObject)
            {
                // 次のラインに進む
                currentTargetLineIndex++;

                // 目的地を更新（次の中間ラインまたは最終ゴール）
                SetDestination();
                return;
            }
        }
    }

    // 目的地を超えたら位置をリセットして再度歩行
    private void ResetPosition()
    {
        if (frontierStart == null || frontierGoal == null || !navMeshAgent.isOnNavMesh) return;
        Vector3 resetPosition = CalculateResetPosition();
        if (navMeshAgent.isActiveAndEnabled) navMeshAgent.Warp(resetPosition); else transform.position = resetPosition;
        SetDestination();
        ApplyRandomMovementParams();
        if (!isStationaryPedestrian)
        {
            AlignWithDestination();
        }
    }

    private bool ShouldUseLeftSide(bool globalIsLeftSideTraffic)
    {
        // 逆走者は左右を反転
        return isReversePedestrian ? !globalIsLeftSideTraffic : globalIsLeftSideTraffic;
    }

    private Vector3 GetSidePositionOnLine(LineObject line, Vector3 forwardDir, bool useLeftSide, float offset)
    {
        Vector3 center = line.GetCenter();
        Vector3 leftDir = new Vector3(-forwardDir.z, 0f, forwardDir.x).normalized;
        return useLeftSide ? center + leftDir * offset : center - leftDir * offset;
    }

    private void ApplyRandomMovementParams()
    {
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
