using UnityEngine;
using UnityEngine.InputSystem;
public class CameraController : MonoBehaviour
{
    [SerializeField] private float moveSpeed = 10f; 
    [SerializeField] private float scrollSensitivity = 0.1f;
    [SerializeField] private float minSpeed = 2f;
    [SerializeField] private float maxSpeed = 50f;
    void Update()
        {
            bool cPress = Keyboard.current.cKey.wasPressedThisFrame;
            AdjustSpeed();
            
            if (cPress) {UpdateCamera();}
            MoveCamera();
            
        }

    // Automatically centers the camera around the plant
    void UpdateCamera()
{
    if (Camera.main == null) return;

    Renderer[] allRenderers = Object.FindObjectsByType<Renderer>();
    
    if (allRenderers.Length == 0) return;

    Bounds bounds = allRenderers[0].bounds;

    for (int i = 1; i < allRenderers.Length; i++)
    {
        bounds.Encapsulate(allRenderers[i].bounds);
    }

    float size = bounds.size.magnitude;
    float fov = Camera.main.fieldOfView;
    float distance = (size * 0.7f) / Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad);

    Vector3 dir = new Vector3(1, 0.7f, -1).normalized;

    Camera.main.transform.position = bounds.center + dir * distance;
    Camera.main.transform.LookAt(bounds.center);
}
    void AdjustSpeed()
    {
        // Get the scroll Y delta
        float scrollDelta = Mouse.current.scroll.ReadValue().y;

        if (scrollDelta != 0)
        {
            moveSpeed += scrollDelta * scrollSensitivity;
            moveSpeed = Mathf.Clamp(moveSpeed, minSpeed, maxSpeed);
        }
    }
    void MoveCamera()
    {
        Vector3 direction = Vector3.zero;

        // Get input values
        float moveForward = 0f;
        float moveSideways = 0f;
        float moveVertical = 0f;

        if (Keyboard.current.wKey.isPressed) moveForward += 1;
        if (Keyboard.current.sKey.isPressed) moveForward -= 1;
        if (Keyboard.current.aKey.isPressed) moveSideways -= 1;
        if (Keyboard.current.dKey.isPressed) moveSideways += 1;
        if (Keyboard.current.eKey.isPressed) moveVertical += 1;
        if (Keyboard.current.qKey.isPressed) moveVertical -= 1;

        // Flatten the forward and right vectors so Y-rotation doesn't affect speed/direction
        Vector3 forward = transform.forward;
        Vector3 right = transform.right;
        forward.y = 0;
        right.y = 0;
        forward.Normalize();
        right.Normalize();

        // Calculate final direction
        direction = (forward * moveForward) + (right * moveSideways) + (Vector3.up * moveVertical);

        // Check if direction is not zero to avoid console warnings with .normalized
        if (direction.sqrMagnitude > 0.001f)
        {
            Camera.main.transform.position += direction.normalized * moveSpeed * Time.deltaTime;
        }
    }
    Vector3 ToUnityVector(PlantRenderer.Vec3 v)
    {
        return new Vector3(v.x, v.y, v.z);
    }
}