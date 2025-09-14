using UnityEngine;

/// <summary>
/// メッシュのバウンディングボックスを基に、最も長い軸方向を「線」とみなし、
/// その両端点（ワールド座標）を算出・保持するコンポーネント。
/// Gizmos で線の可視化も行います。
/// </summary>
[RequireComponent(typeof(MeshFilter))]
public class LineObject : MonoBehaviour
{
    /// <summary>
    /// 左端点（ワールド座標）。
    /// 便宜上の名称で、選ばれた軸の「負方向」側を指します。
    /// </summary>
    public Vector3 LeftPoint { get; private set; }
    /// <summary>
    /// 右端点（ワールド座標）。
    /// 便宜上の名称で、選ばれた軸の「正方向」側を指します。
    /// </summary>
    public Vector3 RightPoint { get; private set; }

    void Awake()
    {
        CalculateEndpoints();
    }

    /// <summary>
    /// メッシュのバウンディングボックス情報を使い、
    /// 最も長い軸方向に沿った両端点を計算して設定します。
    /// </summary>
    public void CalculateEndpoints()
    {
        var meshFilter = GetComponent<MeshFilter>();
        if (meshFilter == null || meshFilter.sharedMesh == null)
        {
            // MeshFilter または Mesh が無い場合は計算できないため、
            // 現在位置を両端点として代入しエラーログを出します。
            Debug.LogError("LineObject requires a MeshFilter with a mesh.", this);
            LeftPoint = transform.position;
            RightPoint = transform.position;
            return;
        }

        var bounds = meshFilter.sharedMesh.bounds;
        var scale = transform.lossyScale;

        // 最長軸を決定する（0: x, 1: y, 2: z）
        // ※ 現在は「スケールの大きさ」を基準に最長軸を選んでいます
        int longestAxis = 0; // 0:x, 1:y, 2:z
        float maxScale = Mathf.Abs(scale.x);
        if (Mathf.Abs(scale.y) > maxScale)
        {
            maxScale = Mathf.Abs(scale.y);
            longestAxis = 1;
        }
        if (Mathf.Abs(scale.z) > maxScale)
        {
            longestAxis = 2;
        }

        // ローカル空間で、最長軸方向の半径ベクトル（中心→端）を作成
        var direction = Vector3.zero;
        direction[longestAxis] = bounds.extents[longestAxis];

        // ローカル空間で始点（負方向側）と終点（正方向側）を算出
        Vector3 localStart = bounds.center - direction;
        Vector3 localEnd = bounds.center + direction;

        // ローカル→ワールド座標へ変換して保持
        LeftPoint = transform.TransformPoint(localStart);
        RightPoint = transform.TransformPoint(localEnd);
    }

    /// <summary>
    /// 両端点の中心位置（ワールド座標）を返します。
    /// </summary>
    public Vector3 GetCenter()
    {
        return (LeftPoint + RightPoint) / 2f;
    }

    /// <summary>
    /// 両端点間の距離（線分の長さ）を返します。
    /// </summary>
    public float GetLength()
    {
        return Vector3.Distance(LeftPoint, RightPoint);
    }

    /// <summary>
    /// 強化学習などで使用する補助関数。
    /// 与えられたワールド座標が、LeftPoint→RightPoint の線分上で
    /// どの位置に相当するかを 0〜1 の比率で返します（0=Left, 1=Right）。
    /// 例えば、エージェントが歩行レーン上にいるかどうかの判定に利用できます。
    /// </summary>
    public float GetLateralRatio(Vector3 worldPos)
    {
        Vector3 a = LeftPoint;
        Vector3 b = RightPoint;
        Vector3 ab = b - a;
        float lenSq = ab.sqrMagnitude;
        float t = Vector3.Dot(worldPos - a, ab) / lenSq;
        return Mathf.Clamp01(t);
    }

    private void OnDrawGizmos()
    {
        // エディタ上では毎フレーム再計算して、変更を即時に可視化
        if (!Application.isPlaying)
        {
            CalculateEndpoints();
        }

        // 未初期化（両端点がゼロ）の場合は描画しない
        if (LeftPoint == Vector3.zero && RightPoint == Vector3.zero) return;

        // 線分は緑、端点は Left=青, Right=赤 で表示
        Gizmos.color = Color.green;
        Gizmos.DrawLine(LeftPoint, RightPoint);

        Gizmos.color = Color.blue;   // 左端点
        Gizmos.DrawSphere(LeftPoint, 0.1f);

        Gizmos.color = Color.red;  // 右端点
        Gizmos.DrawSphere(RightPoint, 0.1f);
    }
}

