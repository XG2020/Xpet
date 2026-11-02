using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.EventSystems;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>Windows alpha composition, model hit testing and draggable desktop window.</summary>
[DefaultExecutionOrder(-1000)]
public partial class TransparentWindowController : MonoBehaviour
{
    [SerializeField] private bool clickThrough = true;
    [SerializeField] private bool topmost = true;
    public bool IsDragging { get; private set; }
    public IntPtr NativeWindowHandle {
        get {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
            return window;
#else
            return IntPtr.Zero;
#endif
        }
    }
    private readonly List<RaycastResult> uiHits = new List<RaycastResult>();
    private PetCharacterAnimation character;
    private DesktopPetAppearance appearance;
    private float walkRemainder;
    private readonly PetHandLeadGesture handLead = new PetHandLeadGesture();
    private readonly PetHandFallMotion handFall = new PetHandFallMotion();
    private float handLeadSuppressUntil;
    private bool quitting;
    public bool ConsumedHandPointer => handLead.Active || Time.unscaledTime < handLeadSuppressUntil;
    private Renderer[] characterRenderers;
    private RectTransform[] visiblePanels;
    private readonly Vector3[] uiCorners = new Vector3[4];
    private void Awake()
    {
        // Imported FBX files may contain Blender cameras. The desktop uses one main view.
        Camera main = Camera.main;
        foreach (Camera camera in FindObjectsByType<Camera>(FindObjectsInactive.Include))
            if (main != null && camera != main) camera.enabled = false;
    }
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
    private const int GWL_STYLE = -16, GWL_EXSTYLE = -20;
    private const long WS_POPUP = 0x80000000L, WS_VISIBLE = 0x10000000L;
    private const long WS_EX_LAYERED = 0x80000, WS_EX_TRANSPARENT = 0x20;
    private const uint SWP_NOSIZE = 1, SWP_NOMOVE = 2, SWP_NOACTIVATE = 0x10, SWP_FRAMECHANGED = 0x20;
    private IntPtr window;
    private bool passing;
    private Point dragCursor, dragWindowOrigin;
    private bool leftWasDown;
    private bool rightWasDown;
    private bool potentialDrag;
    private Point pressCursor;
    private int handGroundTop;
    private float handLeftPosition, handTopPosition;
    private float handLiftSlack, handShoulderOffset;
#if DEVELOPMENT_BUILD
    private float nextPointerDiagnostic;
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern IntPtr GetCapture();
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
#endif
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int left, top, right, bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int size; public Rect monitor; public Rect work; public uint flags; }
    [StructLayout(LayoutKind.Sequential)] private struct Margins { public int left, right, top, bottom; }
    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);
    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr param);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr param);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] private static extern IntPtr GetActiveWindow();
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr value);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr h, ref Point point);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr h, ref Point point);
    [DllImport("user32.dll")] private static extern IntPtr SetCapture(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    private readonly struct PhysicalPixels : IDisposable {
        private readonly IntPtr previous;
        public PhysicalPixels(bool enabled) { previous = enabled ? SetThreadDpiAwarenessContext(new IntPtr(-4)) : IntPtr.Zero; }
        public void Dispose() { if (previous != IntPtr.Zero) SetThreadDpiAwarenessContext(previous); }
    }
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr h, out Rect rect);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out Rect rect);
    [DllImport("user32.dll")] private static extern bool SystemParametersInfo(uint action, uint parameter, out Rect rect, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(Point point, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
#endif
    private void Start()
    {
        Application.runInBackground = true;
        Application.targetFrameRate = 60;
        character = FindAnyObjectByType<PetCharacterAnimation>();
        appearance = GetComponent<DesktopPetAppearance>();
        if (character != null) characterRenderers = character.GetComponentsInChildren<Renderer>(true);
        Transform canvas = GameObject.Find("DesktopPetCanvas")?.transform;
        if (canvas != null) visiblePanels = new[] {
            canvas.Find("ChatPanel") as RectTransform,
            canvas.Find("SettingsOverlay/SettingsCard") as RectTransform,
            canvas.Find("ContextMenuRoot") as RectTransform,
            canvas.Find("SpeechBubble") as RectTransform,
            canvas.Find("HistoryPanel") as RectTransform
        };
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        using var dpi = new PhysicalPixels(true);
        // Preserve per-pixel alpha so a translucent bubble blends with the desktop rather
        // than with a magenta color key. The build enables URP/framebuffer alpha output.
        if (Camera.main != null) {
            Camera.main.clearFlags = CameraClearFlags.SolidColor;
            Camera.main.backgroundColor = Color.clear;
            Camera.main.allowHDR = false;
            Camera.main.allowMSAA = false;
        }
        window = GetActiveWindow();
        uint ownPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
        GetWindowThreadProcessId(window, out uint activePid);
        if (window == IntPtr.Zero || activePid != ownPid) {
            EnumWindows((handle, _) => {
                GetWindowThreadProcessId(handle, out uint pid);
                // A launcher can initially hide the player. Its client area
                // still identifies the render window before we show/style it.
                if (pid != ownPid || !GetClientRect(handle, out Rect candidate) || candidate.right < 64 || candidate.bottom < 64) return true;
                window = handle; return false;
            }, IntPtr.Zero);
        }
        if (window == IntPtr.Zero) return;
        SetWindowLongPtr(window, GWL_STYLE, new IntPtr(WS_POPUP | WS_VISIBLE));
        // Desktop companion windows stay out of the taskbar and Alt+Tab.
        const long toolWindow = 0x80, appWindow = 0x40000;
        long exStyle = (GetWindowLongPtr(window, GWL_EXSTYLE).ToInt64() | WS_EX_LAYERED | toolWindow) & ~appWindow;
        SetWindowLongPtr(window, GWL_EXSTYLE, new IntPtr(exStyle));
        var margins = new Margins { left = -1, right = -1, top = -1, bottom = -1 };
        int compositionResult = DwmExtendFrameIntoClientArea(window, ref margins);
        if (compositionResult != 0) Debug.LogError("Desktop alpha composition failed: " + compositionResult);
        GetWindowRect(window, out Rect initialRect);
        InitializeNativePlacement(initialRect);
#endif
    }
    public void SetClickThrough(bool enabled) { clickThrough = enabled; }
    public void RebindCharacter(PetCharacterAnimation value)
    {
        StopHandLead();
        character = value;
        characterRenderers = value != null ? value.GetComponentsInChildren<Renderer>(true) : Array.Empty<Renderer>();
        IsDragging = false;
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        potentialDrag = false;
        ReleaseCapture();
#endif
    }
    public void RegisterVisiblePanel(RectTransform panel)
    {
        if (panel == null) return;
        var items = new List<RectTransform>(visiblePanels ?? Array.Empty<RectTransform>());
        if (!items.Contains(panel)) { items.Add(panel); visiblePanels = items.ToArray(); }
    }
    private void Update()
    {
#if UNITY_EDITOR && ENABLE_INPUT_SYSTEM
        UpdateEditorHandLead();
#endif
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        using var dpi = new PhysicalPixels(true);
        if (window == IntPtr.Zero) return;
        GetCursorPos(out Point cursor);
        Point client = cursor;
        ScreenToClient(window, ref client);
        GetClientRect(window, out Rect clientRect);
        Vector2 pointer = new Vector2(client.x * Screen.width / (float)Math.Max(1, clientRect.right),
            (clientRect.bottom - client.y) * Screen.height / (float)Math.Max(1, clientRect.bottom));
        bool overUi = IsOverUI(pointer);
        var touch = GetComponent<PetTouchFeedback>();
        string handSide = null;
        bool overHand = !overUi && touch != null && touch.TryGetHandSide(pointer, out handSide);
        Camera view = Camera.main;
        Ray pointerRay = view != null ? view.ScreenPointToRay(pointer) : default;
        RaycastHit modelHit = default;
        bool overModel = view != null && Physics.Raycast(pointerRay, out modelHit, 100,
            Physics.DefaultRaycastLayers, QueryTriggerInteraction.Collide);
        bool leftDown = (GetAsyncKeyState(1) & 0x8000) != 0;
        bool rightDown = (GetAsyncKeyState(2) & 0x8000) != 0;
        bool leftPressed = leftDown && !leftWasDown, leftReleased = !leftDown && leftWasDown;
        bool rightPressed = rightDown && !rightWasDown;
#if ENABLE_INPUT_SYSTEM
        if (Mouse.current != null) {
            leftPressed |= Mouse.current.leftButton.wasPressedThisFrame;
            // Losing focus can reset the Input System mouse and synthesize a release.
            // The global native button remains authoritative while dragging the desktop.
            leftReleased |= !leftDown && Mouse.current.leftButton.wasReleasedThisFrame;
            rightPressed |= Mouse.current.rightButton.wasPressedThisFrame;
        }
#endif
        if (leftPressed || rightPressed)
        {
            GetComponent<DesktopPetUIActions>()?.DismissContextForOutsidePress(pointer);
            if (!overUi) DismissDropdownsForOutsidePress();
        }
        if (leftPressed && !SettingsWorkspaceOpen && !IsDragging && (overModel || overHand) && !overUi && !DesktopPetPanelDrag.AnyDragging) {
            dragCursor = pressCursor = cursor;
            GetWindowRect(window, out Rect initial);
            dragWindowOrigin = new Point { x = initial.left, y = initial.top };
            if (overHand && !handFall.Active) handGroundTop = initial.top;
            if(overHand && touch.TryGetHandLeadGeometry(handSide,out Vector2 shoulder,out float armReach))
            {
                handLiftSlack=Mathf.Max(24,(shoulder.y+armReach-pointer.y)*clientRect.bottom/Mathf.Max(1f,Screen.height));
                handShoulderOffset=(pointer.x-shoulder.x)*clientRect.right/Mathf.Max(1f,Screen.width);
            }
            potentialDrag = true;
            handLead.Press(new Vector2(cursor.x,cursor.y), Time.unscaledTime, handSide);
            SetCapture(window);
        }
        bool pass = clickThrough && !overUi && !overModel && !overHand && !IsDragging && !potentialDrag && !DesktopPetPanelDrag.AnyDragging;
        if (pass != passing) {
            long style = GetWindowLongPtr(window, GWL_EXSTYLE).ToInt64();
            style = pass ? style | WS_EX_TRANSPARENT : style & ~WS_EX_TRANSPARENT;
            SetWindowLongPtr(window, GWL_EXSTYLE, new IntPtr(style));
            passing = pass;
        }
        if (SettingsWorkspaceOpen && handLead.Pending) { ReleaseNativeHandLead(); potentialDrag = false; ReleaseCapture(); }
        if (leftDown && handLead.Pending)
        {
            handLead.Advance(new Vector2(cursor.x,cursor.y), Time.unscaledTime);
            if (handLead.Active && character != null)
            {
                if (!character.IsHandLeading) {
                    GetWindowRect(window,out Rect leadStart);handLeftPosition=leadStart.left;handTopPosition=leadStart.top;
                    handFall.Cancel(); character.BeginHandLead(handLead.Side);
                }
                if (character.IsHandLeading)
                {
                    GetWindowRect(window, out Rect current);
                    float bodyPixels = Vector2.Distance(view.WorldToScreenPoint(character.transform.position),view.WorldToScreenPoint(character.transform.TransformPoint(Vector3.up*1.72f))) * clientRect.bottom/Mathf.Max(1f,Screen.height);
                    float liftProgress=Mathf.Clamp01((pressCursor.y-cursor.y)/Mathf.Max(24,handLiftSlack));
                    float lag = Mathf.Sign(handLead.Velocity)*Mathf.Min(bodyPixels*.20f,Mathf.Abs(handLead.Velocity)*.18f)*(1-liftProgress);
                    float target = dragWindowOrigin.x + cursor.x-pressCursor.x-lag+handShoulderOffset*liftProgress;
                    float targetTop = Mathf.Min(handGroundTop,dragWindowOrigin.y+cursor.y-pressCursor.y+handLiftSlack);
                    float pullSpeed = new Vector2(handLead.Velocity,handLead.VerticalVelocity).magnitude;
                    float speed = Mathf.Min(900,pullSpeed+Vector2.Distance(new Vector2(target,targetTop),new Vector2(current.left,current.top))*4);
                    if(Math.Abs(current.left-Mathf.RoundToInt(handLeftPosition))>1)handLeftPosition=current.left;
                    if(Math.Abs(current.top-Mathf.RoundToInt(handTopPosition))>1)handTopPosition=current.top;
                    handLeftPosition=Mathf.MoveTowards(handLeftPosition,target,speed*Time.unscaledDeltaTime);
                    handTopPosition=Mathf.MoveTowards(handTopPosition,targetTop,speed*Time.unscaledDeltaTime);
                    int left = Mathf.RoundToInt(handLeftPosition), top = Mathf.RoundToInt(handTopPosition);
                    int unclampedLeft=left,unclampedTop=top;
                    ClampVisiblePosition(ref left, ref top, MonitorFromPoint(cursor,2));
                    if(left!=unclampedLeft)handLeftPosition=left;if(top!=unclampedTop)handTopPosition=top;
                    float velocity = (left-current.left)/Mathf.Max(.001f,Time.unscaledDeltaTime);
                    SetWindowPos(window,IntPtr.Zero,left,top,0,0,SWP_NOSIZE|SWP_NOACTIVATE|4);
                    Vector2 handPointer = pointer + new Vector2(-(left-current.left)*Screen.width/(float)Math.Max(1,clientRect.right),(top-current.top)*Screen.height/(float)Math.Max(1,clientRect.bottom));
                    bool airborne = handGroundTop-top>4;
                    character.SetHandLeadPointer(handPointer,airborne,airborne?1:pullSpeed/300f);
                    character.UpdateHandLead(velocity == 0 ? character.WalkingDirection : Mathf.Sign(velocity),Mathf.Abs(velocity),handLead.Running);
                }
            }
        }
        if (leftDown && potentialDrag && !handLead.Pending && !IsDragging &&
            (Math.Abs(cursor.x - pressCursor.x) > 5 || Math.Abs(cursor.y - pressCursor.y) > 5)) IsDragging = true;
        if (IsDragging) {
            handFall.Cancel();
            GetWindowRect(window, out Rect current);
            int dx = cursor.x - dragCursor.x, dy = cursor.y - dragCursor.y;
            int left = dragWindowOrigin.x + cursor.x - pressCursor.x;
            int top = dragWindowOrigin.y + cursor.y - pressCursor.y;
            ClampVisiblePosition(ref left, ref top, MonitorFromPoint(cursor, 2));
            SetWindowPos(window, IntPtr.Zero, left, top, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE | 4);
            if (character != null) character.SetDragging(true, dx);
            dragCursor = cursor;
        } else if (!SettingsWorkspaceOpen && character != null && character.IsWalking && !character.IsHandLeading) {
            walkRemainder += character.WalkingDirection * character.DesktopTravelSpeed * Time.unscaledDeltaTime;
            int step = (int)walkRemainder;
            if (step != 0) {
                walkRemainder -= step;
                GetWindowRect(window, out Rect current);
                int left = current.left + step, top = current.top;
                ClampVisiblePosition(ref left, ref top, ResidentMonitorHandle());
                if (left == current.left) character.StopWalking();
                else SetWindowPos(window, IntPtr.Zero, left, top, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE | 4);
            }
        } else {
            walkRemainder = 0;
        }
        if (leftReleased) {
            bool wasDragging = IsDragging;
            bool wasHandLeading = handLead.Active || (character != null && character.IsHandLeading);
            IsDragging = false;
            if (potentialDrag) ReleaseCapture();
            if (wasHandLeading) ReleaseNativeHandLead();
            else if (wasDragging) character?.SetDragging(false, 0);
            else if (potentialDrag) GetComponent<PetTouchFeedback>()?.TouchAtScreenPoint(pointer);
            handLead.Cancel();
            potentialDrag = false;
            if (wasDragging) SaveNativePlacement();
            else if (wasHandLeading) RememberNativePlacementAfterHandLead();
        }
        if (handFall.Active && !handLead.Active && !IsDragging)
        {
            bool landed=handFall.Step(Time.unscaledDeltaTime);
            GetWindowRect(window,out Rect fallingRect);
            int fallLeft=fallingRect.left,fallTop=handGroundTop-Mathf.RoundToInt(handFall.Height);
            ClampVisiblePosition(ref fallLeft,ref fallTop,ResidentMonitorHandle());
            SetWindowPos(window,IntPtr.Zero,fallLeft,fallTop,0,0,SWP_NOSIZE|SWP_NOACTIVATE|4);
            if(landed) { character?.PlayHandLanding(); SaveNativePlacementAfterLanding(); }
        }
#if DEVELOPMENT_BUILD
        LogPointerDiagnostic(cursor, client, clientRect, pointer, view, pointerRay, modelHit,
            overUi, overModel, leftDown, rightDown, leftPressed || leftReleased || rightPressed);
#endif
        leftWasDown = leftDown;
        rightWasDown = rightDown;
#endif
    }
    private void StopHandLead()
    {
        if (handLead.Active || (character != null && character.IsHandLeading)) handLeadSuppressUntil = Time.unscaledTime + .25f;
        handLead.Cancel(); handFall.Cancel(); character?.EndHandLead(); character?.CancelHandFall();
    }
    private void OnDisable()
    {
        if (quitting) return;
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        using var dpi = new PhysicalPixels(true);
        SaveNativePlacement();
#endif
        StopHandLead();
        if(IsDragging) {IsDragging=false;character?.SetDragging(false,0);}
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        potentialDrag = false; ReleaseCapture();
#endif
    }
    private void OnApplicationQuit()
    {
        quitting = true;
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        using var dpi = new PhysicalPixels(true);
        SaveNativePlacement();
        // Stop mouse/accessibility traffic to the transparent tool window while
        // Unity's native input managers are still alive, before its late flush.
        potentialDrag=false;IsDragging=false;ReleaseCapture();
        if(window!=IntPtr.Zero)ShowWindow(window,0);
#endif
    }
#if UNITY_EDITOR && ENABLE_INPUT_SYSTEM
    private Vector2 editorHandPress;
    private Vector3 editorHandOrigin;
    private float editorHandGroundY;
    private float editorLiftSlack,editorShoulderOffset;
    private void UpdateEditorHandLead()
    {
        if (Mouse.current == null || character == null || Camera.main == null) { StopHandLead(); return; }
        Vector2 pointer = Mouse.current.position.ReadValue();
        var touch = GetComponent<PetTouchFeedback>();
        if (Mouse.current.leftButton.wasPressedThisFrame && !IsOverUI(pointer) && !DesktopPetPanelDrag.AnyDragging
            && touch != null && touch.TryGetHandSide(pointer,out string side))
        {
            handLead.Press(pointer,Time.unscaledTime,side); editorHandPress=pointer; editorHandOrigin=character.transform.position;
            if(!handFall.Active) editorHandGroundY=Camera.main.WorldToScreenPoint(editorHandOrigin).y;
            if(touch.TryGetHandLeadGeometry(side,out Vector2 shoulder,out float armReach))
            {editorLiftSlack=Mathf.Max(24,shoulder.y+armReach-pointer.y);editorShoulderOffset=pointer.x-shoulder.x;}
        }
        if (Mouse.current.leftButton.isPressed && handLead.Pending)
        {
            handLead.Advance(pointer,Time.unscaledTime);
            if (handLead.Active)
            {
                if (!character.IsHandLeading) {handFall.Cancel();character.BeginHandLead(handLead.Side);}
                if (character.IsHandLeading)
                {
                    Vector3 origin = Camera.main.WorldToScreenPoint(editorHandOrigin), current = Camera.main.WorldToScreenPoint(character.transform.position);
                    float height = Vector2.Distance(current,Camera.main.WorldToScreenPoint(character.transform.TransformPoint(Vector3.up*1.72f)));
                    float liftProgress=Mathf.Clamp01((pointer.y-editorHandPress.y)/Mathf.Max(24,editorLiftSlack));
                    float lag=Mathf.Sign(handLead.Velocity)*Mathf.Min(height*.20f,Mathf.Abs(handLead.Velocity)*.18f)*(1-liftProgress);
                    float target = Mathf.Clamp(origin.x+pointer.x-editorHandPress.x-lag+editorShoulderOffset*liftProgress,height*.25f,Screen.width-height*.25f);
                    float targetY=Mathf.Clamp(origin.y+pointer.y-editorHandPress.y-editorLiftSlack,editorHandGroundY,Mathf.Max(editorHandGroundY,Screen.height-height));
                    float pullSpeed=new Vector2(handLead.Velocity,handLead.VerticalVelocity).magnitude;
                    float speed = Mathf.Min(900,pullSpeed+Vector2.Distance(new Vector2(target,targetY),current)*4);
                    float x = Mathf.MoveTowards(current.x,target,speed*Time.unscaledDeltaTime);
                    float y = Mathf.MoveTowards(current.y,targetY,speed*Time.unscaledDeltaTime);
                    float velocity = (x-current.x)/Mathf.Max(.001f,Time.unscaledDeltaTime);
                    character.transform.position = Camera.main.ScreenToWorldPoint(new Vector3(x,y,origin.z));
                    bool airborne=y-editorHandGroundY>4;
                    character.SetHandLeadPointer(pointer,airborne,airborne?1:pullSpeed/300f);
                    character.UpdateHandLead(velocity==0?character.WalkingDirection:Mathf.Sign(velocity),Mathf.Abs(velocity),handLead.Running);
                }
            }
        }
        if (Mouse.current.leftButton.wasReleasedThisFrame || (!Mouse.current.leftButton.isPressed && handLead.Pending))
        {
            if(handLead.Active || character.IsHandLeading)
            {
                handLeadSuppressUntil=Time.unscaledTime+.25f;
                float height=Camera.main.WorldToScreenPoint(character.transform.position).y-editorHandGroundY;
                if(height>2) {handFall.Begin(height);character.BeginHandFall();} else character.EndHandLead();
            }
            handLead.Cancel();
        }
        if(handFall.Active && !handLead.Active)
        {
            bool landed=handFall.Step(Time.unscaledDeltaTime);
            Vector3 at=Camera.main.WorldToScreenPoint(character.transform.position);
            at.y=editorHandGroundY+handFall.Height;character.transform.position=Camera.main.ScreenToWorldPoint(at);
            if(landed)character.PlayHandLanding();
        }
    }
#endif
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
    private void ReleaseNativeHandLead()
    {
        if(handLead.Active || (character!=null && character.IsHandLeading))
        {
            handLeadSuppressUntil=Time.unscaledTime+.25f;
            GetWindowRect(window,out Rect current);
            int groundLeft=current.left,groundTop=handGroundTop;
            ClampVisiblePosition(ref groundLeft,ref groundTop,VisiblePetMonitorHandle());handGroundTop=groundTop;
            float height=handGroundTop-current.top;
            if(height>2) {handFall.Begin(height);character?.BeginHandFall();} else character?.EndHandLead();
        }
        handLead.Cancel();
    }
#endif
    private void LateUpdate()
    {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        using var dpi = new PhysicalPixels(true);
        UpdateNativePlacement();
        if (window == IntPtr.Zero || SettingsWorkspaceOpen || IsDragging || handLead.Active || handFall.Active || DesktopPetPanelDrag.AnyDragging) return;
        GetWindowRect(window, out Rect current);
        int left = current.left, top = current.top;
        ClampVisiblePosition(ref left, ref top, ResidentMonitorHandle());
        if (left != current.left || top != current.top)
            SetWindowPos(window, IntPtr.Zero, left, top, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE | 4);
#endif
    }

#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
#if DEVELOPMENT_BUILD
    // Log only in development players, at most once a second except for button edges.
    // Native desktop, client, render and projected collider coordinates in one record
    // distinguish DPI/viewport errors from missing physics and native capture problems.
    private void LogPointerDiagnostic(Point cursor, Point client, Rect clientRect, Vector2 pointer,
        Camera view, Ray ray, RaycastHit hit, bool overUi, bool overModel,
        bool leftDown, bool rightDown, bool buttonEdge)
    {
        if (!buttonEdge && Time.unscaledTime < nextPointerDiagnostic) return;
        nextPointerDiagnostic = Time.unscaledTime + 1f;
        GetWindowRect(window, out Rect nativeRect);
        var origin = new Point();
        ClientToScreen(window, ref origin);
        long exStyle = GetWindowLongPtr(window, GWL_EXSTYLE).ToInt64();
        string ui = uiHits.Count != 0 && uiHits[0].gameObject != null ? uiHits[0].gameObject.name : "none";
        string projection = "none";
        if (view != null && appearance != null && appearance.TryGetDesktopScreenBounds(out UnityEngine.Rect bounds)) {
            float sx = (clientRect.right - clientRect.left) / (float)Math.Max(1, Screen.width);
            float sy = (clientRect.bottom - clientRect.top) / (float)Math.Max(1, Screen.height);
            projection = $"render:{bounds};desktop:({origin.x + bounds.xMin * sx:F1},{origin.y + (Screen.height - bounds.yMax) * sy:F1})-({origin.x + bounds.xMax * sx:F1},{origin.y + (Screen.height - bounds.yMin) * sy:F1})";
        }
        var colliderInfo = new System.Text.StringBuilder();
        if (character != null && view != null) {
            Collider[] colliders = character.GetComponentsInChildren<Collider>(true);
            colliderInfo.Append("count=").Append(colliders.Length);
            for (int i = 0; i < Math.Min(colliders.Length, 3); i++) {
                Collider collider = colliders[i];
                Bounds world = collider.bounds;
                Vector3 bottom = view.WorldToScreenPoint(new Vector3(world.center.x, world.min.y, world.center.z));
                Vector3 center = view.WorldToScreenPoint(world.center);
                Vector3 top = view.WorldToScreenPoint(new Vector3(world.center.x, world.max.y, world.center.z));
                bool directHit = collider.Raycast(ray, out _, 100);
                colliderInfo.Append($" [{collider.name};enabled={collider.enabled};active={collider.gameObject.activeInHierarchy};layer={collider.gameObject.layer};bounds={world};projected={bottom}/{center}/{top};directHit={directHit}]");
            }
        }
        Debug.Log($"[DesktopPetNative] t={Time.unscaledTime:F2} hwnd={window.ToInt64():X} underCursor={WindowFromPoint(cursor).ToInt64():X} capture={GetCapture().ToInt64():X} dpi={GetDpiForWindow(window)} focus={Application.isFocused} " +
            $"cursor=({cursor.x},{cursor.y}) client=({client.x},{client.y}) pointer={pointer} native=({nativeRect.left},{nativeRect.top},{nativeRect.right},{nativeRect.bottom}) origin=({origin.x},{origin.y}) clientSize=({clientRect.right},{clientRect.bottom}) render=({Screen.width},{Screen.height}) " +
            $"camera={(view != null ? view.name : "none")} viewport={(view != null ? view.pixelRect.ToString() : "none")} ray={ray} " +
            $"overUI={overUi}:{ui} overModel={overModel}:{(hit.collider != null ? hit.collider.name : "none")} buttons={leftDown}/{rightDown} edge={buttonEdge} pending={potentialDrag} dragging={IsDragging} pass={passing} nativeTransparent={(exStyle & WS_EX_TRANSPARENT) != 0} " +
            $"character={(character != null ? character.name : "none")} position={(character != null ? character.transform.position.ToString() : "none")} scale={(character != null ? character.transform.lossyScale.ToString() : "none")} visible={projection} colliders={colliderInfo}");
    }
#endif
    private void ClampVisiblePosition(ref int left, ref int top, IntPtr monitor)
    {
        if (!TryGetAllowedOrigins(monitor, out RectInt range)) return;
        left = Mathf.Clamp(left, range.xMin, range.xMax);
        top = Mathf.Clamp(top, range.yMin, range.yMax);
    }

    private bool TryGetAllowedOrigins(IntPtr monitor, out RectInt range)
    {
        range = default;
        var info = new MonitorInfo { size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return false;
        return TryGetAllowedOrigins(new RectInt(info.work.left, info.work.top,
            info.work.right - info.work.left, info.work.bottom - info.work.top), out range);
    }

    private bool TryGetAllowedOrigins(RectInt workArea, out RectInt range)
    {
        range = default;
        Vector2 min = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
        Vector2 max = new Vector2(float.NegativeInfinity, float.NegativeInfinity);
        Camera camera = Camera.main;
        if (appearance != null && appearance.TryGetDesktopScreenBounds(out UnityEngine.Rect stable)) {
            min = stable.min; max = stable.max;
        } else if (camera != null && characterRenderers != null) foreach (Renderer renderer in characterRenderers) {
            if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;
            Bounds bounds = renderer.bounds;
            for (int i = 0; i < 8; i++) {
                Vector3 corner = bounds.center + Vector3.Scale(bounds.extents,
                    new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                Vector3 screen = camera.WorldToScreenPoint(corner);
                if (screen.z > 0) { min = Vector2.Min(min, screen); max = Vector2.Max(max, screen); }
            }
        }
        // Panels clamp their own placement to the monitor. Opening/dragging a panel
        // must not translate this native window and push the character away.
        if (float.IsInfinity(min.x) || !GetClientRect(window, out Rect clientRect)) return false;
        range = DesktopPetWindowGeometry.AllowedOrigins(
            workArea,
            UnityEngine.Rect.MinMaxRect(min.x, min.y, max.x, max.y),
            new Vector2Int(clientRect.right - clientRect.left, clientRect.bottom - clientRect.top),
            new Vector2Int(Screen.width, Screen.height));
        return true;
    }
#endif
    public bool TryGetWorkAreaInRenderPixels(out UnityEngine.Rect area)
    {
        area = new UnityEngine.Rect(0, 0, Screen.width, Screen.height);
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        using var dpi = new PhysicalPixels(true);
        if (window == IntPtr.Zero) return false;
        var info = new MonitorInfo { size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(ResidentMonitorHandle(), ref info) || !GetClientRect(window, out Rect client)) return false;
        var origin = new Point(); if (!ClientToScreen(window, ref origin)) return false;
        float sx = Screen.width / (float)Math.Max(1, client.right), sy = Screen.height / (float)Math.Max(1, client.bottom);
        area = UnityEngine.Rect.MinMaxRect(Mathf.Max(0, (info.work.left - origin.x) * sx),
            Mathf.Max(0, Screen.height - (info.work.bottom - origin.y) * sy),
            Mathf.Min(Screen.width, (info.work.right - origin.x) * sx),
            Mathf.Min(Screen.height, Screen.height - (info.work.top - origin.y) * sy));
#endif
        return area.width > 0 && area.height > 0;
    }

    public bool IsOverUI(Vector2 pointer)
    {
        if (EventSystem.current == null) return false;
        uiHits.Clear();
        EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current) { position = pointer }, uiHits);
        foreach (var hit in uiHits) {
            if (hit.gameObject == null) continue;
            foreach (var panel in visiblePanels ?? Array.Empty<RectTransform>())
                if (panel != null && panel.gameObject.activeInHierarchy && hit.gameObject.transform.IsChildOf(panel)) return true;
            // TMP dropdown lists can be parented directly to the Canvas. Their
            // full-canvas Blocker is deliberately excluded so other apps remain clickable.
            for (Transform owner = hit.gameObject.transform; owner != null; owner = owner.parent)
                if (owner.name == "Dropdown List" && owner.gameObject.activeInHierarchy) return true;
        }
        return false;
    }

    private void DismissDropdownsForOutsidePress()
    {
        foreach (var panel in visiblePanels ?? Array.Empty<RectTransform>()) {
            if (panel == null || !panel.gameObject.activeInHierarchy) continue;
            foreach (var dropdown in panel.GetComponentsInChildren<TMPro.TMP_Dropdown>())
                if (dropdown.IsExpanded) dropdown.Hide();
        }
        EventSystem.current?.SetSelectedGameObject(null);
    }
}

