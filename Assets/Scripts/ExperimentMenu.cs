using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using TMPro;

/// <summary>
/// World-space VR menu — Quest 3.
///
/// WORKFLOW:
///   Setup phase  → set PID, align corridor walls (no chairs yet)
///                  → press START SESSION
///   TrialReady   → chairs + avatar visible, press START
///   Running      → avatar walks, press ABORT to stop early
///   TrialDone    → see duration, press NEXT TRIAL
///   AllDone      → session log saved
///
/// Controls:
///   Right index trigger  = click button
///   Right grip           = grab and move menu
///   Hold right B (1 s)   = toggle menu hide/show
/// </summary>
[RequireComponent(typeof(TrialManager))]
public class ExperimentMenu : MonoBehaviour
{
    // ── Inspector ─────────────────────────────────────────────────
    [Header("Menu")]
    public float menuDistance      = 1.5f;
    public float menuHeightOffset  = 0f;
    public float menuToggleHoldTime= 1.0f;
    [Header("Calibration")]
    public float nudgeStep  = 0.02f;   // metres per press
    public float verticalNudgeStep = 0.005f; // 5 mm per press
    public float rotateStep = 0.2f;    // degrees per press

    // ── References ────────────────────────────────────────────────
    TrialManager _tm;
    GameObject   _canvasGo;
    Canvas       _canvas;

    // ── Phase ─────────────────────────────────────────────────────
    enum Phase { Setup, SessionReady, TrialReady, Running, TrialDone, AllDone }
    Phase _phase = Phase.Setup;

    // ── UI sections ───────────────────────────────────────────────
    GameObject _setupSection;
    GameObject _trialSection;

    // Setup UI refs
    TMP_Text _lblPid, _lblCalib;

    // Trial UI refs
    TMP_Text _lblTrialInfo, _lblResult;
    Button   _btnStart, _btnAbort, _btnRedo, _btnNext;
    TMP_Text _lblNext;

    // Visibility button (always shown)
    Button   _btnVis;
    TMP_Text _lblVis;

    // ── Corridor calibration state ─────────────────────────────────
    Vector3 _corridorOffset   = Vector3.zero;
    float   _corridorRotation = 0f;
    float   _floorFineOffset  = 0f;

    // ── Timer ─────────────────────────────────────────────────────
    float _trialStartTime;
    float _lastDuration;

    // ── Menu toggle / grab ─────────────────────────────────────────
    bool       _menuVisible = true;
    float      _holdTimer   = 0f;
    bool       _bWasHeld    = false;
    bool       _grabbed     = false;
    Vector3    _grabOffset;
    Quaternion _grabRotOffset;
    bool       _deferRaySetup = false;

    // ─────────────────────────────────────────────────────────────
    void Awake()
    {
        _tm = GetComponent<TrialManager>();
        if (_tm.corridorRoot)
            _corridorOffset = _tm.corridorRoot.position;
        Debug.Log($"[ExperimentMenu] Calibration steps loaded: nudge={nudgeStep:F3} m, rotate={rotateStep:F3} degrees.");
        BuildUI();
        // Default position: in front of corridor entrance
        _canvasGo.transform.position = new Vector3(0.9f, 1.4f, 1.5f);
        _canvasGo.transform.rotation = Quaternion.identity;
        _canvasGo.SetActive(true);

        // Listen for goal reached
        _tm.OnTrialComplete  += _ => OnTrialComplete();
        _tm.OnSessionComplete += OnSessionComplete;

        SetPhase(Phase.Setup);
    }

    void Start()
    {
        if (_deferRaySetup) SetupRay();
    }

    void Update()
    {
        HandleToggleInput();
        HandleGrab();
        HandleControllerClick();
        HandleKeyboard();
    }

    // ─────────────────────────────────────────────────────────────
    //  PHASE MACHINE
    // ─────────────────────────────────────────────────────────────
    void SetPhase(Phase p)
    {
        _phase = p;
        bool isSetup = (p == Phase.Setup);
        _setupSection.SetActive(isSetup);
        _trialSection.SetActive(!isSetup);
        _canvas.worldCamera = Camera.main; // re-assign in case camera wasn't ready in Awake

        RefreshPidLabel();
        RefreshCalibLabel();
        RefreshVisBtn();
        if (!isSetup) RefreshTrialUI();
    }

    void RefreshTrialUI()
    {
        bool sessReady = _phase == Phase.SessionReady;
        bool ready     = _phase == Phase.TrialReady;
        bool running   = _phase == Phase.Running;
        bool done      = _phase == Phase.TrialDone;
        bool allDone   = _phase == Phase.AllDone;

        // Trial info label
        if (allDone)
            _lblTrialInfo.text = "<b>SESSION COMPLETE</b>\nAll 12 trials done — log saved.";
        else if (sessReady)
            _lblTrialInfo.text = $"Session P{_tm.ParticipantId:D2} ready\nPress <b>LOAD TRIAL 1</b> when positioned";
        else if (_tm.CurrentLayout >= 1)
            _lblTrialInfo.text = $"Trial  <b>{_tm.CurrentTrialIndex + 1} / 12</b>   "
                               + $"Layout <b>P{_tm.CurrentLayout:D2}</b>";
        else
            _lblTrialInfo.text = "Session active";

        // Result label — shown only after a trial finishes
        _lblResult.gameObject.SetActive(done);
        if (done) _lblResult.text = $"<color=#55ff88>✓  {_lastDuration:F1} s</color>";

        // Buttons
        Show(_btnStart, ready);
        Show(_btnAbort, running);
        Show(_btnRedo,  ready || done);
        // _btnNext = "LOAD TRIAL 1" in SessionReady, "NEXT TRIAL" in TrialDone
        Show(_btnNext,  (sessReady || done) && !allDone);
        if (_lblNext != null)
        {
            if (sessReady)          _lblNext.text = "LOAD TRIAL 1  ▶";
            else if (_tm.CurrentTrialIndex >= 11) _lblNext.text = "FINISH SESSION";
            else                    _lblNext.text = "NEXT TRIAL  ▶";
        }
    }

    static void Show(Button b, bool v) { if (b) b.gameObject.SetActive(v); }

    // ─────────────────────────────────────────────────────────────
    //  ACTIONS
    // ─────────────────────────────────────────────────────────────
    void ActPidMinus()  { _tm.ParticipantId = Mathf.Max(1,  _tm.ParticipantId - 1); RefreshPidLabel(); }
    void ActPidPlus()   { _tm.ParticipantId = Mathf.Min(99, _tm.ParticipantId + 1); RefreshPidLabel(); }

    void ActBeginSession()
    {
        _tm.BeginSession();
        // Don't load layout yet — user still needs to confirm position.
        // Chairs appear only when they press LOAD TRIAL.
        SetPhase(Phase.SessionReady);
    }

    void ActStart()
    {
        _trialStartTime = Time.time;
        _tm.StartTrial();
        SetPhase(Phase.Running);
    }

    void ActAbort()
    {
        _lastDuration = Time.time - _trialStartTime;
        _tm.AbortTrial();
        SetPhase(Phase.TrialDone);
    }

    void ActRedo()
    {
        _tm.RedoCurrentTrial();
        SetPhase(Phase.TrialReady);
    }

    void ActNextTrial()
    {
        bool ok = _tm.LoadNextLayout();
        SetPhase(ok ? Phase.TrialReady : Phase.AllDone);
    }

    void ActToggleVis()
    {
        _tm.SetVirtualVisible(!_tm.VirtualVisible);
        RefreshVisBtn();
    }

    // Called by TrialManager event when avatar reaches goal
    void OnTrialComplete()
    {
        _lastDuration = Time.time - _trialStartTime;
        SetPhase(Phase.TrialDone);
    }

    void OnSessionComplete() => SetPhase(Phase.AllDone);

    // ── Corridor calibration ──────────────────────────────────────
    void ActXLeft()   => Nudge(-nudgeStep);
    void ActXRight()  => Nudge( nudgeStep);
    void ActZBack()   => NudgeZ(-nudgeStep);
    void ActZForward()=> NudgeZ( nudgeStep);
    void ActYDown()   => NudgeY(-verticalNudgeStep);
    void ActYUp()     => NudgeY( verticalNudgeStep);
    void ActRotCCW()  => Rotate(-rotateStep);
    void ActRotCW()   => Rotate( rotateStep);

    void Nudge(float dx)
    {
        _corridorOffset.x += dx;               // Y never changes
        if (_tm.corridorRoot)
        {
            // Change horizontal calibration only; preserve the MRUK-detected floor Y.
            Vector3 position = _tm.corridorRoot.position;
            position.x = _corridorOffset.x;
            _tm.corridorRoot.position = position;
        }
        RefreshCalibLabel();
        ReloadIfSafe();
    }

    void NudgeY(float dy)
    {
        _floorFineOffset += dy;
        if (_tm.corridorRoot)
        {
            Vector3 position = _tm.corridorRoot.position;
            position.y += dy;
            _tm.corridorRoot.position = position;
        }
        RefreshCalibLabel();
        ReloadIfSafe();
    }

    void NudgeZ(float dz)
    {
        _corridorOffset.z += dz;
        if (_tm.corridorRoot)
        {
            // Change depth calibration only; preserve the MRUK-detected floor Y.
            Vector3 position = _tm.corridorRoot.position;
            position.z = _corridorOffset.z;
            _tm.corridorRoot.position = position;
        }
        RefreshCalibLabel();
        ReloadIfSafe();
    }

    void Rotate(float deg)
    {
        _corridorRotation += deg;
        if (_tm.corridorRoot) _tm.corridorRoot.rotation = Quaternion.Euler(0f, _corridorRotation, 0f);
        RefreshCalibLabel();
        ReloadIfSafe();
    }

    void ReloadIfSafe()
    {
        if (_tm.CurrentLayout >= 1 && !_tm.TrialRunning)
            _tm.ReloadCurrentLayout();
    }

    // ── Label refresh ─────────────────────────────────────────────
    void RefreshPidLabel()
    {
        if (_lblPid) _lblPid.text = $"Participant  <b>{_tm.ParticipantId:D2}</b>";
    }

    void RefreshCalibLabel()
    {
        if (_lblCalib)
            _lblCalib.text = $"X {_corridorOffset.x:+0.00;-0.00}  Z {_corridorOffset.z:+0.00;-0.00}  " +
                             $"Yfix {_floorFineOffset:+0.00;-0.00} m  Rot {_corridorRotation:+0.0;-0.0}°";
    }

    void RefreshVisBtn()
    {
        if (!_lblVis) return;
        bool v = _tm.VirtualVisible;
        _lblVis.text = v ? "HIDE VIRTUAL" : "SHOW VIRTUAL";
        if (_btnVis)
        {
            var c = _btnVis.colors;
            c.normalColor = v ? new Color(0.15f, 0.45f, 0.15f) : new Color(0.50f, 0.15f, 0.15f);
            _btnVis.colors = c;
            _btnVis.targetGraphic.color = c.normalColor;
        }
    }

    // ─────────────────────────────────────────────────────────────
    //  CONTROLLER INPUT
    // ─────────────────────────────────────────────────────────────
    void HandleToggleInput()
    {
        bool held = OVRInput.Get(OVRInput.Button.Two, OVRInput.Controller.RTouch);
        if (held)
        {
            _holdTimer += Time.deltaTime;
            if (_holdTimer >= menuToggleHoldTime && !_bWasHeld)
            { _bWasHeld = true; ToggleMenu(); }
        }
        else { _holdTimer = 0f; _bWasHeld = false; }
    }

    void HandleGrab()
    {
        if (!_menuVisible) return;
        Vector3    cp = ControllerWorldPos();
        Quaternion cr = ControllerWorldRot();
        if (OVRInput.GetDown(OVRInput.Button.PrimaryHandTrigger, OVRInput.Controller.RTouch))
        { _grabbed = true; _grabOffset = _canvasGo.transform.position - cp; _grabRotOffset = Quaternion.Inverse(cr) * _canvasGo.transform.rotation; }
        if (_grabbed && OVRInput.Get(OVRInput.Button.PrimaryHandTrigger, OVRInput.Controller.RTouch))
        { _canvasGo.transform.position = cp + _grabOffset; _canvasGo.transform.rotation = cr * _grabRotOffset; }
        if (OVRInput.GetUp(OVRInput.Button.PrimaryHandTrigger, OVRInput.Controller.RTouch))
            _grabbed = false;
    }

    void HandleControllerClick()
    {
        if (!_menuVisible) return;
        if (!OVRInput.GetDown(OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.RTouch)) return;

        Vector3 pos = ControllerWorldPos();
        Vector3 fwd = ControllerWorldRot() * Vector3.forward;

        // Intersect with canvas plane
        var plane = new Plane(-_canvasGo.transform.forward, _canvasGo.transform.position);
        if (!plane.Raycast(new Ray(pos, fwd), out float dist)) return;
        Vector3 hit = pos + fwd * dist;

        Camera cam = Camera.main;
        if (!cam) return;
        var ped  = new PointerEventData(EventSystem.current) { position = cam.WorldToScreenPoint(hit) };
        var hits = new List<RaycastResult>();
        EventSystem.current.RaycastAll(ped, hits);
        foreach (var h in hits)
        {
            var btn = h.gameObject.GetComponent<Button>()
                   ?? h.gameObject.GetComponentInParent<Button>();
            if (btn && btn.interactable && btn.gameObject.activeInHierarchy)
            { btn.onClick.Invoke(); break; }
        }
    }

    void HandleKeyboard()
    {
        if (Input.GetKeyDown(KeyCode.Tab)) ToggleMenu();
        if (Input.GetKeyDown(KeyCode.Return) && _phase == Phase.Setup)     ActBeginSession();
        if (Input.GetKeyDown(KeyCode.Space))
        {
            if (_phase == Phase.TrialReady) ActStart();
            else if (_phase == Phase.Running)   ActAbort();
            else if (_phase == Phase.TrialDone) ActNextTrial();
        }
        if (Input.GetKeyDown(KeyCode.R) && (_phase == Phase.TrialReady || _phase == Phase.TrialDone)) ActRedo();
        if (Input.GetKeyDown(KeyCode.Equals)) ActPidPlus();
        if (Input.GetKeyDown(KeyCode.Minus))  ActPidMinus();
    }

    void ToggleMenu()
    {
        _menuVisible = !_menuVisible;
        _canvasGo.SetActive(_menuVisible);
        if (_menuVisible)
        {
            _grabbed = false;
            var cam = Camera.main;
            if (cam)
            {
                Vector3 fwd = cam.transform.forward; fwd.y = 0f;
                if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.forward;
                fwd.Normalize();
                _canvasGo.transform.position = cam.transform.position + fwd * menuDistance + Vector3.up * menuHeightOffset;
                _canvasGo.transform.rotation = Quaternion.LookRotation(fwd, Vector3.up);
            }
        }
    }

    // ── Controller world-space helpers ────────────────────────────
    Vector3 ControllerWorldPos()
    {
        var p = OVRInput.GetLocalControllerPosition(OVRInput.Controller.RTouch);
        var o = Camera.main?.transform.parent;
        return o ? o.TransformPoint(p) : p;
    }

    Quaternion ControllerWorldRot()
    {
        var r = OVRInput.GetLocalControllerRotation(OVRInput.Controller.RTouch);
        var o = Camera.main?.transform.parent;
        return o ? o.rotation * r : r;
    }

    // ── Laser / ray setup ─────────────────────────────────────────
    void SetupRay()
    {
        var rig = FindObjectOfType<OVRCameraRig>();
        if (!rig) return;
        var es = FindObjectOfType<EventSystem>();
        if (es)
        {
            var im = es.GetComponent<OVRInputModule>();
            if (im) { im.rayTransform = rig.rightControllerAnchor; im.joyPadClickButton = OVRInput.Button.PrimaryIndexTrigger; }
        }
        var ra = rig.rightControllerAnchor;
        if (ra && !ra.GetComponent<LaserPointer>()) ra.gameObject.AddComponent<LaserPointer>();
        _deferRaySetup = false;
    }

    // ─────────────────────────────────────────────────────────────
    //  BUILD UI
    // ─────────────────────────────────────────────────────────────
    void BuildUI()
    {
        // EventSystem
        var es = FindObjectOfType<EventSystem>();
        if (!es) { var eg = new GameObject("EventSystem"); es = eg.AddComponent<EventSystem>(); eg.AddComponent<OVRInputModule>(); }
        else { var s = es.GetComponent<StandaloneInputModule>(); if (s) Destroy(s); if (!es.GetComponent<OVRInputModule>()) es.gameObject.AddComponent<OVRInputModule>(); }

        // Laser
        var rig = FindObjectOfType<OVRCameraRig>();
        if (rig)
        {
            var im = es.GetComponent<OVRInputModule>();
            if (im) { im.rayTransform = rig.rightControllerAnchor; im.joyPadClickButton = OVRInput.Button.PrimaryIndexTrigger; }
            var ra = rig.rightControllerAnchor;
            if (ra && !ra.GetComponent<LaserPointer>()) ra.gameObject.AddComponent<LaserPointer>();
        }
        else _deferRaySetup = true;

        // Canvas
        _canvasGo = new GameObject("ExperimentCanvas");
        _canvasGo.transform.localScale = Vector3.one * 0.002f;
        _canvas = _canvasGo.AddComponent<Canvas>();
        var cv = _canvas;
        cv.renderMode = RenderMode.WorldSpace; cv.worldCamera = Camera.main;
        _canvasGo.AddComponent<CanvasScaler>();
        _canvasGo.AddComponent<GraphicRaycaster>();

        // Root panel
        var root = Panel(_canvasGo.transform, 460f, new Color(0.07f, 0.07f, 0.11f, 0.97f));

        Lbl(root, "AVATAR NAVIGATION  EXPERIMENT", 15, Color.cyan, TextAlignmentOptions.Center);
        Div(root);

        // ── Always-visible visibility toggle ──────────────────────
        _btnVis = Btn(root, "HIDE VIRTUAL", 0f, ActToggleVis, new Color(0.15f, 0.45f, 0.15f));
        _lblVis = _btnVis.GetComponentInChildren<TMP_Text>();
        H(_btnVis.gameObject, 40f);
        Div(root);

        // ── SETUP SECTION ─────────────────────────────────────────
        _setupSection = Section(root, "Setup");

        Lbl(_setupSection, "PARTICIPANT", 12, Amber(), TextAlignmentOptions.Left);
        var pr = Row(_setupSection);
        Btn(pr, "−", 44f, ActPidMinus); _lblPid = Lbl(pr, "P01", 14, Color.white, TextAlignmentOptions.Center); W(_lblPid.gameObject, 150f); Btn(pr, "+", 44f, ActPidPlus);
        Div(_setupSection);

        Lbl(_setupSection, "CORRIDOR ALIGNMENT", 12, Amber(), TextAlignmentOptions.Left);
        _lblCalib = Lbl(_setupSection, "X +0.00  Z +0.00  Yfix +0.00 m  Rot +0.0°", 11, Color.grey, TextAlignmentOptions.Center);
        var sr = Row(_setupSection);
        Btn(sr, "◄  Left",  0f, ActXLeft);  Btn(sr, "Right  ►", 0f, ActXRight);
        var zr = Row(_setupSection);
        Btn(zr, "▼  Back", 0f, ActZBack); Btn(zr, "Forward  ▲", 0f, ActZForward);
        var yr = Row(_setupSection);
        Btn(yr, "↓  Lower", 0f, ActYDown); Btn(yr, "Raise  ↑", 0f, ActYUp);
        var rr = Row(_setupSection);
        Btn(rr, "↺  CCW",   0f, ActRotCCW); Btn(rr, "CW  ↻",   0f, ActRotCW);
        Div(_setupSection);

        var bBegin = Btn(_setupSection, "START SESSION  ▶", 0f, ActBeginSession, new Color(0.18f, 0.58f, 0.18f));
        H(bBegin.gameObject, 50f);

        // ── TRIAL SECTION ─────────────────────────────────────────
        _trialSection = Section(root, "Trial");

        _lblTrialInfo = Lbl(_trialSection, "Trial info", 14, Color.white, TextAlignmentOptions.Center);
        _lblResult    = Lbl(_trialSection, "", 15, Color.white, TextAlignmentOptions.Center);
        Div(_trialSection);

        // START / ABORT / REDO on one row
        var rr2 = Row(_trialSection);
        _btnStart = Btn(rr2, "START", 0f, ActStart, new Color(0.12f, 0.52f, 0.12f));
        _btnAbort = Btn(rr2, "ABORT", 0f, ActAbort, new Color(0.55f, 0.12f, 0.12f));
        _btnRedo  = Btn(rr2, "REDO",  0f, ActRedo,  new Color(0.38f, 0.28f, 0.08f));
        H(rr2, 46f);

        _btnNext = Btn(_trialSection, "NEXT TRIAL  ▶", 0f, ActNextTrial, new Color(0.18f, 0.38f, 0.75f));
        _lblNext = _btnNext.GetComponentInChildren<TMP_Text>();
        H(_btnNext.gameObject, 50f);

        // ── Footer ────────────────────────────────────────────────
        Div(root);
        Lbl(root, "Hold B (1 s) to toggle  ·  Grip to move", 9, Color.grey, TextAlignmentOptions.Center);
    }

    // ─────────────────────────────────────────────────────────────
    //  UI HELPERS  (short names to keep BuildUI readable)
    // ─────────────────────────────────────────────────────────────
    static Color Amber() => new Color(1f, 0.78f, 0.28f);

    static GameObject Panel(Transform parent, float w, Color bg)
    {
        var go = new GameObject("Panel"); go.transform.SetParent(parent, false);
        go.AddComponent<Image>().color = bg;
        go.GetComponent<RectTransform>().sizeDelta = new Vector2(w, 0f);
        var vl = go.AddComponent<VerticalLayoutGroup>();
        vl.padding = new RectOffset(12, 12, 12, 12); vl.spacing = 6f;
        vl.childForceExpandWidth = true; vl.childForceExpandHeight = false;
        vl.childControlWidth = true;    vl.childControlHeight = true;
        go.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        return go;
    }

    static GameObject Section(GameObject parent, string name)
    {
        var go = new GameObject(name); go.transform.SetParent(parent.transform, false);
        var vl = go.AddComponent<VerticalLayoutGroup>();
        vl.spacing = 6f; vl.childForceExpandWidth = true; vl.childForceExpandHeight = false;
        vl.childControlWidth = true; vl.childControlHeight = true;
        go.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        go.AddComponent<LayoutElement>().flexibleWidth = 1f;
        return go;
    }

    static TMP_Text Lbl(GameObject p, string txt, int sz, Color col, TextAlignmentOptions al)
    {
        var go = new GameObject("L"); go.transform.SetParent(p.transform, false);
        var t = go.AddComponent<TextMeshProUGUI>(); t.text = txt; t.fontSize = sz; t.color = col; t.alignment = al;
        go.AddComponent<LayoutElement>().preferredHeight = sz * 1.6f;
        return t;
    }
    static TMP_Text Lbl(Transform p, string txt, int sz, Color col, TextAlignmentOptions al) => Lbl(p.gameObject, txt, sz, col, al);

    static Button Btn(GameObject p, string lbl, float fw, UnityEngine.Events.UnityAction act, Color? bg = null)
    {
        var go = new GameObject("B_" + lbl); go.transform.SetParent(p.transform, false);
        Color bgc = bg ?? new Color(0.24f, 0.24f, 0.28f);
        var img = go.AddComponent<Image>(); img.color = bgc;
        var btn = go.AddComponent<Button>(); btn.onClick.AddListener(act);
        var c = btn.colors; c.normalColor = bgc; c.highlightedColor = bgc * 1.35f; c.pressedColor = bgc * 0.60f; c.disabledColor = new Color(0.2f, 0.2f, 0.2f, 0.35f);
        btn.colors = c; btn.targetGraphic = img;
        var tgo = new GameObject("T"); tgo.transform.SetParent(go.transform, false);
        var tmp = tgo.AddComponent<TextMeshProUGUI>(); tmp.text = lbl; tmp.fontSize = 12; tmp.color = Color.white; tmp.alignment = TextAlignmentOptions.Center;
        var tr = tgo.GetComponent<RectTransform>(); tr.anchorMin = Vector2.zero; tr.anchorMax = Vector2.one; tr.offsetMin = tr.offsetMax = Vector2.zero;
        var le = go.AddComponent<LayoutElement>(); le.preferredHeight = 36f;
        if (fw > 0f) le.preferredWidth = fw; else le.flexibleWidth = 1f;
        return btn;
    }
    static Button Btn(Transform p, string lbl, float fw, UnityEngine.Events.UnityAction act, Color? bg = null) => Btn(p.gameObject, lbl, fw, act, bg);

    static GameObject Row(GameObject p)
    {
        var go = new GameObject("Row"); go.transform.SetParent(p.transform, false);
        var hl = go.AddComponent<HorizontalLayoutGroup>();
        hl.spacing = 6f; hl.childForceExpandWidth = false; hl.childForceExpandHeight = true;
        hl.childControlWidth = true; hl.childControlHeight = true;
        go.AddComponent<LayoutElement>().preferredHeight = 40f;
        return go;
    }

    static void Div(GameObject p)
    {
        var go = new GameObject("D"); go.transform.SetParent(p.transform, false);
        go.AddComponent<Image>().color = new Color(0.4f, 0.4f, 0.5f, 0.45f);
        go.AddComponent<LayoutElement>().preferredHeight = 1f;
    }

    static void H(GameObject go, float h) { var le = go.GetComponent<LayoutElement>() ?? go.AddComponent<LayoutElement>(); le.preferredHeight = h; }
    static void W(GameObject go, float w) { var le = go.GetComponent<LayoutElement>() ?? go.AddComponent<LayoutElement>(); le.preferredWidth  = w; }
}
