using UnityEngine;

[RequireComponent(typeof(MeshFilter))]
public class LineObject : MonoBehaviour
{
    public Vector3 LeftPoint { get; private set; }
    public Vector3 RightPoint { get; private set; }

    void Awake()
    {
        CalculateEndpoints();
    }

    // Public method to allow recalculation if needed
    public void CalculateEndpoints()
    {
        var meshFilter = GetComponent<MeshFilter>();
        if (meshFilter == null || meshFilter.sharedMesh == null)
        {
            Debug.LogError("LineObject requires a MeshFilter with a mesh.", this);
            LeftPoint = transform.position;
            RightPoint = transform.position;
            return;
        }

        var bounds = meshFilter.sharedMesh.bounds;
        var scale = transform.lossyScale;

        // Find the axis with the largest scale component.
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

        // Create a direction vector along the longest axis in local space
        var direction = Vector3.zero;
        direction[longestAxis] = bounds.extents[longestAxis];

        // Calculate local start and end points
        Vector3 localStart = bounds.center - direction;
        Vector3 localEnd = bounds.center + direction;

        // Transform points to world space
        LeftPoint = transform.TransformPoint(localStart);
        RightPoint = transform.TransformPoint(localEnd);
    }

    public Vector3 GetCenter()
    {
        return (LeftPoint + RightPoint) / 2f;
    }

    public float GetLength()
    {
        return Vector3.Distance(LeftPoint, RightPoint);
    }

    public Vector3 GetDirection()
    {
        return (RightPoint - LeftPoint).normalized;
    }

    // 強化学習用関数
    // 与えられたワールド座標が LeftPoint→RightPoint のどの位置か (0=Left,1=Right)
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
        // Always recalculate in editor to show changes instantly
        if (!Application.isPlaying)
        {
            CalculateEndpoints();
        }

        if(LeftPoint == Vector3.zero && RightPoint == Vector3.zero) return;

        Gizmos.color = Color.green;
        Gizmos.DrawLine(LeftPoint, RightPoint);
        Gizmos.DrawSphere(LeftPoint, 0.1f);
        Gizmos.DrawSphere(RightPoint, 0.1f);
    }
}
