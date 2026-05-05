using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(CharacterController))]
public sealed class TinyFirstPersonController : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private Transform cameraPivot;

    [Header("Look")]
    [SerializeField] private float mouseSensitivity = 0.08f;
    [SerializeField] private float minPitch = -85f;
    [SerializeField] private float maxPitch = 85f;

    [Header("Movement")]
    [SerializeField] private float walkSpeed = 1.65f;
    [SerializeField] private float sprintSpeed = 2.55f;
    [SerializeField] private float crouchSpeed = 0.95f;
    [SerializeField] private float acceleration = 18f;
    [SerializeField] private float jumpHeight = 0.42f;
    [SerializeField] private float gravity = -12f;

    [Header("Tiny Body")]
    [SerializeField] private float standingHeight = 0.55f;
    [SerializeField] private float crouchingHeight = 0.32f;
    [SerializeField] private float standingEyeHeight = 0.43f;
    [SerializeField] private float crouchingEyeHeight = 0.25f;
    [SerializeField] private float crouchLerpSpeed = 14f;

    [Header("Camera Bob")]
    [SerializeField] private float walkBobSpeed = 14f;
    [SerializeField] private float sprintBobSpeed = 19f;
    [SerializeField] private float crouchBobSpeed = 7f;
    [SerializeField] private float walkBobAmount = 0.018f;
    [SerializeField] private float sprintBobAmount = 0.025f;
    [SerializeField] private float crouchBobAmount = 0.009f;
    [SerializeField] private float bobReturnSpeed = 18f;

    [Header("Climbing")]
    [SerializeField] private float climbDuration = 0.68f;
    [SerializeField] private float climbLiftHeight = 0.12f;
    [SerializeField] private float climbCameraPull = 0.035f;
    [SerializeField] private float climbLookInfluence = 0.65f;
    [SerializeField, Range(90f, 170f)] private float maxClimbFacingAngle = 115f;
    [SerializeField] private LayerMask landingClearanceMask = ~0;

    [Header("Debug")]
    [SerializeField] private bool showClimbZonesInGame;
    [SerializeField] private Color climbZoneDebugColor = new Color(0.1f, 0.65f, 1f, 0.95f);
    [SerializeField] private Color landingZoneDebugColor = new Color(0.2f, 1f, 0.45f, 0.95f);

    private static Material debugLineMaterial;
    private CharacterController controller;
    private readonly List<ClimbZone> climbZones = new List<ClimbZone>();
    private Vector3 horizontalVelocity;
    private float verticalVelocity;
    private float currentEyeHeight;
    private float bobTimer;
    private float bobOffset;
    private float pitch;
    private bool isClimbing;
    private float climbCameraOffset;

    private void Awake()
    {
        controller = GetComponent<CharacterController>();

        if (cameraPivot == null && Camera.main != null)
        {
            cameraPivot = Camera.main.transform;
        }

        ConfigureTinyBody(standingHeight);
        currentEyeHeight = standingEyeHeight;
        ApplyCameraHeight(true);
    }

    private void OnEnable()
    {
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }

    private void OnDisable()
    {
        climbZones.Clear();
    }

    private void Update()
    {
        if (Keyboard.current == null)
        {
            return;
        }

        if (Keyboard.current.escapeKey.wasPressedThisFrame)
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
        else if (Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame)
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

        if (isClimbing)
        {
            Look(climbLookInfluence);
            ApplyCameraHeight(false);
            return;
        }

        if (Keyboard.current.eKey.wasPressedThisFrame && TryGetBestClimbRoute(out ClimbZone.Route route))
        {
            StartCoroutine(ClimbTo(route));
            return;
        }

        Look(1f);
        Move();
        UpdateCrouch();
        UpdateCameraBob();
    }

    public void RegisterClimbZone(ClimbZone zone)
    {
        if (zone != null && !climbZones.Contains(zone))
        {
            climbZones.Add(zone);
        }
    }

    public void UnregisterClimbZone(ClimbZone zone)
    {
        climbZones.Remove(zone);
    }

    private void OnTriggerEnter(Collider other)
    {
        RegisterClimbZone(other.GetComponentInParent<ClimbZone>());
    }

    private void OnTriggerExit(Collider other)
    {
        UnregisterClimbZone(other.GetComponentInParent<ClimbZone>());
    }

    private void OnRenderObject()
    {
        if (!showClimbZonesInGame || !EnsureDebugLineMaterial())
        {
            return;
        }

        debugLineMaterial.SetPass(0);
        GL.PushMatrix();

        IReadOnlyList<ClimbZone> zones = ClimbZone.RegisteredZones;
        for (int i = 0; i < zones.Count; i++)
        {
            ClimbZone zone = zones[i];
            if (zone == null || !zone.isActiveAndEnabled)
            {
                continue;
            }

            DrawRuntimeWireBox(zone.VolumeCollider, climbZoneDebugColor);

            if (zone.LandingZoneOverride != null)
            {
                DrawRuntimeWireBox(zone.LandingZoneOverride, landingZoneDebugColor);
            }
        }

        GL.PopMatrix();
    }

    private void Look(float sensitivityMultiplier)
    {
        if (Mouse.current == null || cameraPivot == null || Cursor.lockState != CursorLockMode.Locked)
        {
            return;
        }

        Vector2 mouseDelta = Mouse.current.delta.ReadValue();
        transform.Rotate(Vector3.up * (mouseDelta.x * mouseSensitivity * sensitivityMultiplier));

        pitch = Mathf.Clamp(pitch - mouseDelta.y * mouseSensitivity * sensitivityMultiplier, minPitch, maxPitch);
        cameraPivot.localRotation = Quaternion.Euler(pitch, 0f, 0f);
    }

    private void Move()
    {
        Vector2 input = ReadMoveInput();
        Vector3 targetDirection = transform.right * input.x + transform.forward * input.y;
        targetDirection = Vector3.ClampMagnitude(targetDirection, 1f);

        bool isCrouching = IsCrouchHeld();
        float targetSpeed = isCrouching ? crouchSpeed : IsSprintHeld() ? sprintSpeed : walkSpeed;
        Vector3 targetVelocity = targetDirection * targetSpeed;
        horizontalVelocity = Vector3.Lerp(horizontalVelocity, targetVelocity, acceleration * Time.deltaTime);

        if (controller.isGrounded && verticalVelocity < 0f)
        {
            verticalVelocity = -1.5f;
        }

        if (controller.isGrounded && !isCrouching && Keyboard.current.spaceKey.wasPressedThisFrame)
        {
            verticalVelocity = Mathf.Sqrt(jumpHeight * -2f * gravity);
        }

        verticalVelocity += gravity * Time.deltaTime;

        Vector3 velocity = horizontalVelocity + Vector3.up * verticalVelocity;
        controller.Move(velocity * Time.deltaTime);
    }

    private bool TryGetBestClimbRoute(out ClimbZone.Route bestRoute)
    {
        bestRoute = default;
        float bestScore = float.PositiveInfinity;

        for (int i = climbZones.Count - 1; i >= 0; i--)
        {
            ClimbZone zone = climbZones[i];
            if (zone == null)
            {
                climbZones.RemoveAt(i);
                continue;
            }

            if (!zone.ContainsPlayer(transform.position, controller))
            {
                climbZones.RemoveAt(i);
                continue;
            }

            if (!zone.TryGetRoute(transform.position, controller, out ClimbZone.Route route))
            {
                continue;
            }

            if (!CanClimbFromCurrentFacing(route))
            {
                continue;
            }

            if (!HasLandingClearance(route.LandingPosition))
            {
                continue;
            }

            float score = (route.GrabPosition - transform.position).sqrMagnitude
                + (route.LandingPosition - transform.position).sqrMagnitude * 0.15f;

            if (score < bestScore)
            {
                bestScore = score;
                bestRoute = route;
            }
        }

        return bestScore < float.PositiveInfinity;
    }

    private bool CanClimbFromCurrentFacing(ClimbZone.Route route)
    {
        Vector3 climbDirection = Vector3.ProjectOnPlane(route.LandingPosition - transform.position, Vector3.up);
        Vector3 lookDirection = Vector3.ProjectOnPlane(transform.forward, Vector3.up);

        if (climbDirection.sqrMagnitude < 0.0001f || lookDirection.sqrMagnitude < 0.0001f)
        {
            return true;
        }

        float angle = Vector3.Angle(lookDirection, climbDirection);
        return angle <= maxClimbFacingAngle;
    }

    private bool HasLandingClearance(Vector3 feetPosition)
    {
        float radius = controller.radius * 0.9f;
        Vector3 bottom = feetPosition + Vector3.up * (radius + 0.02f);
        Vector3 top = feetPosition + Vector3.up * (standingHeight - radius);

        Collider[] hits = Physics.OverlapCapsule(bottom, top, radius, landingClearanceMask, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < hits.Length; i++)
        {
            if (!hits[i].transform.IsChildOf(transform))
            {
                return false;
            }
        }

        return true;
    }

    private IEnumerator ClimbTo(ClimbZone.Route route)
    {
        isClimbing = true;
        horizontalVelocity = Vector3.zero;
        verticalVelocity = 0f;
        bobTimer = 0f;
        bobOffset = 0f;
        climbCameraOffset = 0f;
        currentEyeHeight = standingEyeHeight;
        ConfigureTinyBody(standingHeight);

        controller.enabled = false;

        Vector3 start = transform.position;
        Vector3 grab = route.GrabPosition;
        Vector3 landing = route.LandingPosition;
        Vector3 liftPoint = new Vector3(grab.x, Mathf.Max(grab.y, landing.y) + climbLiftHeight, grab.z);
        float elapsed = 0f;

        while (elapsed < climbDuration)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / climbDuration);
            float eased = Mathf.SmoothStep(0f, 1f, t);

            if (t < 0.42f)
            {
                transform.position = Vector3.Lerp(start, grab, Mathf.SmoothStep(0f, 1f, t / 0.42f));
            }
            else if (t < 0.72f)
            {
                transform.position = Vector3.Lerp(grab, liftPoint, Mathf.SmoothStep(0f, 1f, (t - 0.42f) / 0.3f));
            }
            else
            {
                transform.position = Vector3.Lerp(liftPoint, landing, Mathf.SmoothStep(0f, 1f, (t - 0.72f) / 0.28f));
            }

            climbCameraOffset = -Mathf.Sin(eased * Mathf.PI) * climbCameraPull;
            yield return null;
        }

        transform.position = landing;
        controller.enabled = true;
        climbCameraOffset = 0f;
        currentEyeHeight = standingEyeHeight;
        ApplyCameraHeight(true);
        isClimbing = false;
    }

    private void UpdateCrouch()
    {
        bool isCrouching = IsCrouchHeld();
        float targetHeight = isCrouching ? crouchingHeight : standingHeight;
        float targetEyeHeight = isCrouching ? crouchingEyeHeight : standingEyeHeight;

        ConfigureTinyBody(Mathf.Lerp(controller.height, targetHeight, crouchLerpSpeed * Time.deltaTime));
        currentEyeHeight = Mathf.Lerp(currentEyeHeight, targetEyeHeight, crouchLerpSpeed * Time.deltaTime);
    }

    private void UpdateCameraBob()
    {
        if (cameraPivot == null)
        {
            return;
        }

        bool isMoving = ReadMoveInput().sqrMagnitude > 0.01f && horizontalVelocity.sqrMagnitude > 0.01f;
        bool isCrouching = IsCrouchHeld();
        bool isGrounded = controller.isGrounded;

        if (isMoving && isGrounded)
        {
            float bobSpeed = isCrouching ? crouchBobSpeed : IsSprintHeld() ? sprintBobSpeed : walkBobSpeed;
            float bobAmount = isCrouching ? crouchBobAmount : IsSprintHeld() ? sprintBobAmount : walkBobAmount;

            bobTimer += Time.deltaTime * bobSpeed;
            bobOffset = Mathf.Sin(bobTimer) * bobAmount;
        }
        else
        {
            bobTimer = 0f;
            bobOffset = Mathf.Lerp(bobOffset, 0f, bobReturnSpeed * Time.deltaTime);
        }

        ApplyCameraHeight(false);
    }

    private Vector2 ReadMoveInput()
    {
        Vector2 input = Vector2.zero;

        if (Keyboard.current.dKey.isPressed)
        {
            input.x += 1f;
        }

        if (Keyboard.current.qKey.isPressed || Keyboard.current.aKey.isPressed)
        {
            input.x -= 1f;
        }

        if (Keyboard.current.zKey.isPressed || Keyboard.current.wKey.isPressed)
        {
            input.y += 1f;
        }

        if (Keyboard.current.sKey.isPressed)
        {
            input.y -= 1f;
        }

        return Vector2.ClampMagnitude(input, 1f);
    }

    private bool IsSprintHeld()
    {
        return Keyboard.current.leftShiftKey.isPressed || Keyboard.current.rightShiftKey.isPressed;
    }

    private bool IsCrouchHeld()
    {
        return Keyboard.current.leftCtrlKey.isPressed
            || Keyboard.current.rightCtrlKey.isPressed
            || Keyboard.current.cKey.isPressed;
    }

    private void ConfigureTinyBody(float height)
    {
        controller.height = height;
        controller.radius = 0.16f;
        controller.center = Vector3.up * (height * 0.5f);
        controller.stepOffset = 0.12f;
    }

    private void ApplyCameraHeight(bool snap)
    {
        if (cameraPivot == null)
        {
            return;
        }

        Vector3 targetPosition = new Vector3(0f, currentEyeHeight + bobOffset + climbCameraOffset, 0f);
        cameraPivot.localPosition = snap
            ? targetPosition
            : Vector3.Lerp(cameraPivot.localPosition, targetPosition, crouchLerpSpeed * Time.deltaTime);
    }

    private static bool EnsureDebugLineMaterial()
    {
        if (debugLineMaterial != null)
        {
            return true;
        }

        Shader shader = Shader.Find("Hidden/Internal-Colored");
        if (shader == null)
        {
            return false;
        }

        debugLineMaterial = new Material(shader)
        {
            hideFlags = HideFlags.HideAndDontSave
        };

        debugLineMaterial.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        debugLineMaterial.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        debugLineMaterial.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
        debugLineMaterial.SetInt("_ZWrite", 0);
        return true;
    }

    private static void DrawRuntimeWireBox(BoxCollider box, Color color)
    {
        if (box == null || !box.enabled)
        {
            return;
        }

        Matrix4x4 matrix = box.transform.localToWorldMatrix;
        Vector3 center = box.center;
        Vector3 extents = box.size * 0.5f;

        Vector3 p0 = matrix.MultiplyPoint3x4(center + new Vector3(-extents.x, -extents.y, -extents.z));
        Vector3 p1 = matrix.MultiplyPoint3x4(center + new Vector3(extents.x, -extents.y, -extents.z));
        Vector3 p2 = matrix.MultiplyPoint3x4(center + new Vector3(extents.x, -extents.y, extents.z));
        Vector3 p3 = matrix.MultiplyPoint3x4(center + new Vector3(-extents.x, -extents.y, extents.z));
        Vector3 p4 = matrix.MultiplyPoint3x4(center + new Vector3(-extents.x, extents.y, -extents.z));
        Vector3 p5 = matrix.MultiplyPoint3x4(center + new Vector3(extents.x, extents.y, -extents.z));
        Vector3 p6 = matrix.MultiplyPoint3x4(center + new Vector3(extents.x, extents.y, extents.z));
        Vector3 p7 = matrix.MultiplyPoint3x4(center + new Vector3(-extents.x, extents.y, extents.z));

        GL.Begin(GL.LINES);
        GL.Color(color);
        DrawLine(p0, p1);
        DrawLine(p1, p2);
        DrawLine(p2, p3);
        DrawLine(p3, p0);
        DrawLine(p4, p5);
        DrawLine(p5, p6);
        DrawLine(p6, p7);
        DrawLine(p7, p4);
        DrawLine(p0, p4);
        DrawLine(p1, p5);
        DrawLine(p2, p6);
        DrawLine(p3, p7);
        GL.End();
    }

    private static void DrawLine(Vector3 start, Vector3 end)
    {
        GL.Vertex(start);
        GL.Vertex(end);
    }
}
