using System.Collections.Generic;
using UnityEngine;

// SNOW RUSH — downhill snowboard slalom. One-touch arcade.
// Auto-forward down an endless descending piste; the ONLY control is left/right steer
// (arrows / A,D / hold-or-drag pointer & touch). Carve through slalom gates for score+combo,
// dodge trees & rocks, hit ramps for big air. Speed ramps up over time. 3 wipeouts -> result.
//
// Built entirely in code (CreatePrimitive + a couple of procedural meshes) so it renders
// reliably in WebGL with engine-code stripping disabled. NO Rigidbody/colliders: the player
// is pure Transform-driven and all hit-tests are distance checks at the pass-line (the closest
// approach for a forward-moving course). Coexists with Juice (sfx/bgm/particles) & AutoShot.
public class SnowRush : MonoBehaviour
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        Application.runInBackground = true;
        var go = new GameObject("__SnowRush");
        go.AddComponent<SnowRush>();
        DontDestroyOnLoad(go);
    }

    // ---- scene refs ----
    Transform player;      // root that advances down the slope (no roll)
    Transform visual;      // child that banks (rolls) with steer
    Transform cam;
    Camera camComp;
    TextMesh hudScore, hudBest, hudSpeed, combeText, bannerText, dbg;
    readonly List<Mountain> mountains = new List<Mountain>();

    // ---- spawned course objects ----
    class Ob { public Transform t; public int kind; public float x, z, r; public bool passed; } // kind 0 tree,1 rock,2 ramp
    class Gate { public Transform L, R, beam; public float x, z, half; public bool passed; }
    readonly List<Ob> obs = new List<Ob>();
    readonly List<Gate> gates = new List<Gate>();
    readonly List<EdgeMark> edges = new List<EdgeMark>();
    class EdgeMark { public Transform t; public float z; }

    // ---- run state ----
    enum State { Playing, Crash, GameOver }
    State state = State.Playing;
    float playerX, steerVel;          // current lateral pos & smoothed steer velocity
    float speed = START_SPEED;        // forward m/s
    float elapsed;                    // seconds in current run (drives difficulty)
    float airY, airVel;               // vertical jump offset above the slope
    bool Airborne => airY > 0.01f;
    float stun;                       // crash stun timer (no steering, slowed)
    float fovPunch;                   // transient FOV add (boost/air)
    int score, best, combo, lives = MAX_LIVES;
    float comboFlash, bannerTimer;
    // HUD layout adapts to aspect ratio (Unity's vertical FOV is fixed => portrait is much narrower)
    float hudScale = 1f, halfH = 2.7f, halfW = 4.6f;
    const float HUD_Z = 6.5f;
    float genZ;                       // course generation frontier (world z, negative = downhill)
    int rowIndex;
    float steerInput;                 // -1..1 resolved each frame

    // ---- tuning ----
    const float START_SPEED = 17f, MAX_SPEED = 46f, RAMP_TIME = 70f;
    const float SLOPE_TAN = 0.26f;    // descend amount per unit z
    const float HALF_WIDTH = 9f;      // piste half-width (player clamp)
    const float ROW = 11f;            // z spacing between generated rows
    const float HORIZON = 150f;       // generate this far ahead
    const float DESPAWN = 22f;        // remove this far behind
    const int MAX_LIVES = 3;
    const float CRASH_DX = 1.4f;      // lateral hit radius vs obstacles
    const float NEAR_DX = 3.2f;       // within this (but > crash) = near-miss bonus
    float trailT;

    // debug
    bool showDbg;
    int dbgPasses, dbgNear;

    // ===================================================================== boot
    void Start()
    {
        // strip the default scene's camera + light so we don't double-light or shoot the wrong cam
        foreach (var c in FindObjectsByType<Camera>(FindObjectsSortMode.None)) Destroy(c.gameObject);
        foreach (var l in FindObjectsByType<Light>(FindObjectsSortMode.None)) Destroy(l.gameObject);

        best = PlayerPrefs.GetInt("snowrush_best", 0);
        BuildEnvironment();
        BuildCamera();
        BuildPlayer();
        BuildHud();
        BuildMountains();
        // pre-fill the course so the first frame already looks alive
        genZ = -ROW;
        while (genZ > -HORIZON) GenerateRow();
    }

    // ===================================================================== materials / meshes
    static Material Mat(Color c, float metallic = 0f, float smooth = 0.1f, bool emissive = false)
    {
        var sh = Shader.Find("Universal Render Pipeline/Lit");
        if (sh == null) sh = Shader.Find("Standard");
        var m = new Material(sh);
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
        if (m.HasProperty("_Color")) m.SetColor("_Color", c);
        if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", metallic);
        if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", smooth);
        if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", smooth);
        if (emissive && m.HasProperty("_EmissionColor"))
        {
            m.EnableKeyword("_EMISSION");
            m.SetColor("_EmissionColor", c * 0.6f);
        }
        return m;
    }

    static GameObject Prim(PrimitiveType pt, Transform parent, Vector3 lpos, Vector3 lscale, Color c, Material shared = null)
    {
        var g = GameObject.CreatePrimitive(pt);
        var col = g.GetComponent<Collider>(); if (col != null) Destroy(col);   // pure distance hit-tests
        g.transform.SetParent(parent, false);
        g.transform.localPosition = lpos;
        g.transform.localScale = lscale;
        g.GetComponent<Renderer>().sharedMaterial = shared != null ? shared : Mat(c);
        return g;
    }

    static Mesh _cone;
    static Mesh ConeMesh()
    {
        if (_cone != null) return _cone;
        int seg = 10; var v = new List<Vector3>(); var tri = new List<int>();
        v.Add(new Vector3(0, 1f, 0));                       // apex (0)
        for (int i = 0; i < seg; i++)
        {
            float a = i * Mathf.PI * 2f / seg;
            v.Add(new Vector3(Mathf.Cos(a) * 0.5f, 0f, Mathf.Sin(a) * 0.5f));
        }
        int baseC = v.Count; v.Add(Vector3.zero);            // base center
        for (int i = 0; i < seg; i++)
        {
            int a = 1 + i, b = 1 + (i + 1) % seg;
            tri.Add(0); tri.Add(b); tri.Add(a);              // side
            tri.Add(baseC); tri.Add(a); tri.Add(b);          // base
        }
        _cone = new Mesh(); _cone.SetVertices(v); _cone.SetTriangles(tri, 0);
        _cone.RecalculateNormals(); _cone.RecalculateBounds();
        return _cone;
    }

    static Mesh _wedge;
    static Mesh WedgeMesh()
    {
        if (_wedge != null) return _wedge;
        // ramp rising toward -Z (downhill-forward). Unit: x[-0.5,0.5], y[0,1], z[-0.5,0.5]
        var v = new Vector3[] {
            new Vector3(-0.5f,0,0.5f), new Vector3(0.5f,0,0.5f),     // back-bottom 0,1
            new Vector3(-0.5f,0,-0.5f), new Vector3(0.5f,0,-0.5f),   // front-bottom 2,3
            new Vector3(-0.5f,1f,-0.5f), new Vector3(0.5f,1f,-0.5f), // front-top 4,5
        };
        var tri = new int[] {
            0,4,2, 0,1,5, 0,5,4,        // left slope... (slope face spans back-bottom to front-top)
            1,3,5, 5,3,4,               // sides + front
            0,2,3, 0,3,1,               // bottom
        };
        _wedge = new Mesh(); _wedge.vertices = v; _wedge.triangles = tri;
        _wedge.RecalculateNormals(); _wedge.RecalculateBounds();
        return _wedge;
    }

    GameObject MeshObj(Mesh m, Transform parent, Vector3 lpos, Vector3 lscale, Color c, Material shared = null)
    {
        var g = new GameObject("m");
        g.transform.SetParent(parent, false);
        g.transform.localPosition = lpos; g.transform.localScale = lscale;
        g.AddComponent<MeshFilter>().sharedMesh = m;
        g.AddComponent<MeshRenderer>().sharedMaterial = shared != null ? shared : Mat(c);
        return g;
    }

    float SurfaceY(float z) => -z * SLOPE_TAN;   // z is <=0 downhill; surface descends as we go

    // ===================================================================== world
    Material snowMat, treeMat, trunkMat, rockMat, redMat, blueMat, rampMat, riderMat, boardMat;

    void BuildEnvironment()
    {
        snowMat  = Mat(new Color(0.90f, 0.94f, 1.00f), 0f, 0.05f);
        treeMat  = Mat(new Color(0.13f, 0.45f, 0.30f), 0f, 0.05f);
        trunkMat = Mat(new Color(0.34f, 0.22f, 0.13f), 0f, 0.05f);
        rockMat  = Mat(new Color(0.52f, 0.55f, 0.60f), 0f, 0.12f);
        redMat   = Mat(new Color(0.95f, 0.18f, 0.22f), 0f, 0.2f, true);
        blueMat  = Mat(new Color(0.20f, 0.45f, 1.00f), 0f, 0.2f, true);
        rampMat  = Mat(new Color(0.70f, 0.82f, 1.00f), 0.1f, 0.4f);
        riderMat = Mat(new Color(1.00f, 0.55f, 0.10f), 0f, 0.2f);
        boardMat = Mat(new Color(0.10f, 0.85f, 0.95f), 0.2f, 0.6f, true);

        var sun = new GameObject("Sun").AddComponent<Light>();
        sun.type = LightType.Directional;
        sun.color = new Color(1f, 0.97f, 0.88f);
        sun.intensity = 1.15f;
        sun.transform.rotation = Quaternion.Euler(42f, 28f, 0f);
        sun.shadows = LightShadows.Soft;

        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
        RenderSettings.ambientSkyColor    = new Color(0.62f, 0.74f, 0.95f);
        RenderSettings.ambientEquatorColor= new Color(0.70f, 0.78f, 0.92f);
        RenderSettings.ambientGroundColor = new Color(0.55f, 0.60f, 0.70f);

        // cool atmospheric fog: depth + hides spawn pop-in, blends piste into the sky
        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.Linear;
        RenderSettings.fogColor = new Color(0.80f, 0.88f, 0.98f);
        RenderSettings.fogStartDistance = 55f;
        RenderSettings.fogEndDistance = 155f;

        // the piste: a very long, wide strip. Centered under the run (which goes toward -Z).
        var ground = new GameObject("Piste");
        var g = GameObject.CreatePrimitive(PrimitiveType.Cube);
        var gc = g.GetComponent<Collider>(); if (gc != null) Destroy(gc);
        g.transform.SetParent(ground.transform, false);
        // rotate the strip to lie along the slope: tilt about X so its top face faces up-slope
        float ang = Mathf.Atan(SLOPE_TAN) * Mathf.Rad2Deg;
        ground.transform.rotation = Quaternion.Euler(-ang, 0, 0);
        g.transform.localScale = new Vector3(HALF_WIDTH * 2f + 6f, 1f, 4000f);
        g.transform.localPosition = new Vector3(0, -0.5f, -1800f); // extends far downhill
        g.GetComponent<Renderer>().sharedMaterial = snowMat;
    }

    void BuildCamera()
    {
        var cgo = new GameObject("MainCamera");
        cgo.tag = "MainCamera";
        camComp = cgo.AddComponent<Camera>();
        camComp.clearFlags = CameraClearFlags.SolidColor;
        camComp.backgroundColor = new Color(0.74f, 0.85f, 0.97f);
        camComp.fieldOfView = 62f;
        camComp.farClipPlane = 400f;
        cgo.AddComponent<AudioListener>();
        cam = cgo.transform;
        cam.position = new Vector3(0, 6f, 10f);
        cam.rotation = Quaternion.Euler(12f, 180f, 0f);  // look toward -Z (downhill)
    }

    void BuildPlayer()
    {
        player = new GameObject("Player").transform;
        player.position = new Vector3(0, SurfaceY(0), 0);
        visual = new GameObject("Visual").transform;
        visual.SetParent(player, false);

        // board
        Prim(PrimitiveType.Cube, visual, new Vector3(0, 0.12f, 0), new Vector3(0.7f, 0.12f, 2.4f), default, boardMat);
        // rider: legs, torso, head (vivid suit for visibility), facing downhill (-Z)
        Prim(PrimitiveType.Capsule, visual, new Vector3(0, 0.75f, -0.05f), new Vector3(0.5f, 0.55f, 0.5f), default, riderMat);
        Prim(PrimitiveType.Capsule, visual, new Vector3(0, 1.35f, -0.02f), new Vector3(0.42f, 0.42f, 0.42f), default, riderMat);
        Prim(PrimitiveType.Sphere,  visual, new Vector3(0, 1.78f, 0f), new Vector3(0.34f, 0.34f, 0.34f), new Color(0.95f, 0.85f, 0.7f));
        // a little tilt forward = speed posture
        visual.localRotation = Quaternion.Euler(8f, 0, 0);
    }

    void BuildMountains()
    {
        var pal = new[] {
            new Color(0.86f, 0.90f, 0.97f), new Color(0.78f, 0.84f, 0.93f), new Color(0.82f, 0.87f, 0.95f)
        };
        for (int i = 0; i < 7; i++)
        {
            var go = MeshObj(ConeMesh(), null, Vector3.zero,
                new Vector3(Random.Range(40f, 75f), Random.Range(35f, 60f), Random.Range(40f, 75f)),
                pal[i % pal.Length]);
            mountains.Add(new Mountain { t = go.transform, ox = (i - 3) * 38f + Random.Range(-10f, 10f), oz = Random.Range(150f, 230f) });
        }
    }
    class Mountain { public Transform t; public float ox, oz; }

    // ===================================================================== HUD
    TextMesh MakeText(float size, Color c, TextAnchor anchor, Vector3 lpos)
    {
        var t = new GameObject("T").AddComponent<TextMesh>();
        t.fontSize = 96; t.characterSize = size; t.color = c; t.anchor = anchor;
        t.alignment = TextAlignment.Center;
        t.transform.SetParent(cam, false);
        t.transform.localPosition = lpos;
        t.transform.localRotation = Quaternion.identity; // child of cam => readable (orientation is relative)
        return t;
    }

    void BuildHud()
    {
        // text is a child of the camera => identity rotation reads correctly. Positions/sizes are
        // set every frame by AdjustHud() so the layout fits any aspect ratio (incl. phone portrait).
        hudScore = MakeText(0.085f, Color.white, TextAnchor.UpperLeft,  Vector3.zero);
        hudBest  = MakeText(0.060f, new Color(0.85f,0.92f,1f), TextAnchor.UpperRight, Vector3.zero);
        hudSpeed = MakeText(0.050f, new Color(0.9f,0.95f,1f), TextAnchor.LowerRight, Vector3.zero);
        combeText= MakeText(0.12f, new Color(1f,0.9f,0.3f), TextAnchor.MiddleCenter, Vector3.zero);
        bannerText=MakeText(0.15f, Color.white, TextAnchor.MiddleCenter, Vector3.zero);
        dbg = MakeText(0.040f, new Color(0.6f,1f,0.7f), TextAnchor.LowerLeft, Vector3.zero);
        dbg.gameObject.SetActive(false);
        combeText.text = ""; bannerText.text = "";
        AdjustHud();
        RefreshHud();
    }

    // Recompute HUD anchor positions + text scale from the current frustum (FOV varies with speed,
    // aspect varies with the window). Vertical extent is FOV-bound; horizontal = vertical * aspect.
    void AdjustHud()
    {
        if (camComp == null) return;
        float aspect = Mathf.Max(0.3f, camComp.aspect);
        halfH = HUD_Z * Mathf.Tan(camComp.fieldOfView * 0.5f * Mathf.Deg2Rad);
        halfW = halfH * aspect;
        // Size text as a constant fraction of the VISIBLE width (halfW). The HUD sits on a world
        // plane, so its on-screen size otherwise tracks the zoom — and portrait's narrow horizontal
        // FOV zooms in hard. Referencing halfW keeps the HUD identical on screen across aspect & FOV.
        const float REF_HALFW = 6.0f;   // landscape (1.6) @ fov 60 — where the base sizes were tuned
        hudScale = Mathf.Clamp(halfW / REF_HALFW, 0.16f, 1.3f);
        float ix = halfW * 0.95f, iy = halfH * 0.93f;

        hudScore.transform.localPosition = new Vector3(-ix, iy, HUD_Z); hudScore.characterSize = 0.085f * hudScale;
        hudBest.transform.localPosition  = new Vector3( ix, iy, HUD_Z); hudBest.characterSize  = 0.060f * hudScale;
        hudSpeed.transform.localPosition = new Vector3( ix, -iy, HUD_Z); hudSpeed.characterSize = 0.050f * hudScale;
        dbg.transform.localPosition      = new Vector3(-ix, -iy * 0.62f, HUD_Z); dbg.characterSize = 0.040f * hudScale;
        combeText.transform.localPosition = new Vector3(0, halfH * 0.5f, HUD_Z);
        if (comboFlash <= 0f) combeText.characterSize = 0.12f * hudScale;
    }

    void RefreshHud()
    {
        if (hudScore) hudScore.text = "SCORE  " + score;
        if (hudBest)  hudBest.text = "BEST  " + best + "\n" + Hearts();
        if (hudSpeed) hudSpeed.text = Mathf.RoundToInt(speed * 3.6f) + " km/h";
    }
    string Hearts() { string s = ""; for (int i = 0; i < MAX_LIVES; i++) s += i < lives ? "♥" : "·"; return s; }

    // ===================================================================== course generation
    void GenerateRow()
    {
        float z = genZ;
        float diff = Mathf.Clamp01(elapsed / RAMP_TIME);
        rowIndex++;

        // edge markers every row (orange/blue alternating) — strong speed cue + course framing
        SpawnEdge(z);

        if (rowIndex % 2 == 0)
        {
            // GATE row
            float half = Mathf.Lerp(3.4f, 2.2f, diff);                 // gap shrinks with difficulty
            float spread = Mathf.Lerp(3.5f, 6.0f, diff);
            float cx = Mathf.Clamp(Mathf.Sin(rowIndex * 0.7f) * spread + Random.Range(-1.2f, 1.2f), -HALF_WIDTH + half + 0.5f, HALF_WIDTH - half - 0.5f);
            SpawnGate(cx, z, half);
        }
        else
        {
            // OBSTACLE row: 1..3 trees/rocks, biased away from the gate corridor
            int n = 1 + Mathf.FloorToInt(diff * 2.5f + Random.value);
            for (int i = 0; i < n; i++)
            {
                float x = Random.Range(-HALF_WIDTH + 1f, HALF_WIDTH - 1f);
                int kind = Random.value < 0.6f ? 0 : 1;
                SpawnObstacle(x, z + Random.Range(-3f, 3f), kind);
            }
        }

        // RAMP occasionally
        if (rowIndex % 9 == 4)
            SpawnObstacle(Random.Range(-4f, 4f), z, 2);

        genZ -= ROW;
    }

    void SpawnEdge(float z)
    {
        for (int side = -1; side <= 1; side += 2)
        {
            var go = new GameObject("edge");
            go.transform.position = new Vector3(side * (HALF_WIDTH + 1.2f), SurfaceY(z) + 0.6f, z);
            bool orange = ((int)(z / ROW)) % 2 == 0;
            Prim(PrimitiveType.Cylinder, go.transform, new Vector3(0, 0.6f, 0), new Vector3(0.18f, 0.6f, 0.18f),
                orange ? new Color(1f, 0.5f, 0.05f) : new Color(0.2f, 0.5f, 1f));
            edges.Add(new EdgeMark { t = go.transform, z = z });
        }
    }

    void SpawnGate(float cx, float z, float half)
    {
        var g = new Gate { x = cx, z = z, half = half };
        float y = SurfaceY(z);
        g.L = MakeFlag(cx - half, z, y, true);
        g.R = MakeFlag(cx + half, z, y, false);
        gates.Add(g);
    }

    Transform MakeFlag(float x, float z, float y, bool red)
    {
        var go = new GameObject("flag");
        go.transform.position = new Vector3(x, y, z);
        Prim(PrimitiveType.Cylinder, go.transform, new Vector3(0, 1.5f, 0), new Vector3(0.12f, 1.5f, 0.12f), new Color(0.9f, 0.9f, 0.9f));
        Prim(PrimitiveType.Cube, go.transform, new Vector3(red ? 0.55f : -0.55f, 2.5f, 0), new Vector3(1.1f, 0.8f, 0.08f), default, red ? redMat : blueMat);
        return go.transform;
    }

    void SpawnObstacle(float x, float z, int kind)
    {
        x = Mathf.Clamp(x, -HALF_WIDTH + 0.6f, HALF_WIDTH - 0.6f);
        var go = new GameObject(kind == 0 ? "tree" : kind == 1 ? "rock" : "ramp");
        float y = SurfaceY(z);
        go.transform.position = new Vector3(x, y, z);
        var ob = new Ob { t = go.transform, kind = kind, x = x, z = z };

        if (kind == 0) // pine tree: trunk + 3 stacked cones
        {
            Prim(PrimitiveType.Cylinder, go.transform, new Vector3(0, 0.6f, 0), new Vector3(0.28f, 0.6f, 0.28f), default, trunkMat);
            MeshObj(ConeMesh(), go.transform, new Vector3(0, 1.0f, 0), new Vector3(2.0f, 1.7f, 2.0f), default, treeMat);
            MeshObj(ConeMesh(), go.transform, new Vector3(0, 2.0f, 0), new Vector3(1.6f, 1.5f, 1.6f), default, treeMat);
            MeshObj(ConeMesh(), go.transform, new Vector3(0, 2.9f, 0), new Vector3(1.1f, 1.3f, 1.1f), default, treeMat);
            // snow caps
            MeshObj(ConeMesh(), go.transform, new Vector3(0, 2.95f, 0), new Vector3(1.12f, 0.5f, 1.12f), new Color(0.95f, 0.97f, 1f));
            ob.r = 1.0f;
        }
        else if (kind == 1) // rock: a couple of rotated grey cubes
        {
            var a = Prim(PrimitiveType.Cube, go.transform, new Vector3(0, 0.5f, 0), new Vector3(1.6f, 1.1f, 1.5f), default, rockMat);
            a.transform.localRotation = Quaternion.Euler(Random.Range(-12f, 12f), Random.Range(0, 360f), Random.Range(-12f, 12f));
            var b = Prim(PrimitiveType.Cube, go.transform, new Vector3(0.5f, 0.3f, -0.3f), new Vector3(1.0f, 0.8f, 1.0f), default, rockMat);
            b.transform.localRotation = Quaternion.Euler(Random.Range(0, 360f), Random.Range(0, 360f), Random.Range(0, 360f));
            ob.r = 1.1f;
        }
        else // ramp (kind 2): wedge of snow you launch off
        {
            MeshObj(WedgeMesh(), go.transform, new Vector3(0, 0, 0), new Vector3(4.5f, 1.6f, 4.0f), default, rampMat);
            ob.r = 2.6f; // wider so it's easy to hit
        }
        obs.Add(ob);
    }

    // ===================================================================== input
    void GatherInput()
    {
        float key = Input.GetAxisRaw("Horizontal");   // arrows + A/D
        float pointer = 0f;
        bool pressed = false; float px = 0f;
        if (Input.touchCount > 0)
        {
            pressed = true; px = Input.GetTouch(0).position.x;
        }
        else if (Input.GetMouseButton(0))
        {
            pressed = true; px = Input.mousePosition.x;
        }
        if (pressed)
        {
            // proportional drag steer: pointer position relative to screen center
            float n = (px / Mathf.Max(1f, Screen.width)) * 2f - 1f; // -1..1
            pointer = Mathf.Clamp(n * 1.8f, -1f, 1f);
        }
        float raw = Mathf.Abs(key) > 0.01f ? key : pointer;

        // attract auto-pilot until the first real input (nice for the in-engine screenshot/demo)
        if (Mathf.Abs(raw) > 0.01f || Input.anyKeyDown) attract = false;
        if (attract && state == State.Playing) raw = AutoSteer();

        steerInput = raw;
    }
    bool attract = true;

    float AutoSteer()
    {
        // steer toward the next gate centre, but dodge the nearest obstacle just ahead
        float target = 0f; float gz = -9999f;
        foreach (var g in gates) if (!g.passed && g.z < player.position.z && g.z > gz) { gz = g.z; target = g.x; }
        // screen-convention aim (+ = screen-right). Moving toward a higher world-x means going screen-left.
        float aim = Mathf.Clamp((playerX - target) / 4f, -1f, 1f);
        foreach (var o in obs)
        {
            if (o.passed || o.kind == 2) continue;
            float dz = player.position.z - o.t.position.z;   // ahead of player => 0..14
            if (dz > -2f && dz < 14f && Mathf.Abs(o.x - playerX) < 2.6f)
                aim = (o.x >= playerX) ? 1f : -1f;            // steer away from the obstacle
        }
        return aim;
    }

    // ===================================================================== main loop
    void Update()
    {
        float dt = Time.deltaTime;
        if (dt > 0.05f) dt = 0.05f;   // clamp big hitches (tab-out) so nothing tunnels

        if (Input.GetKeyDown(KeyCode.F1)) { showDbg = !showDbg; dbg.gameObject.SetActive(showDbg); }

        if (state == State.GameOver)
        {
            if (Input.anyKeyDown || Input.GetMouseButtonDown(0) || Input.touchCount > 0) { attract = false; Restart(); }
            UpdateCamera(dt); UpdateMountains();
            return;
        }

        GatherInput();
        elapsed += dt;

        // ---- speed: ramp up with time; crash temporarily slows ----
        float baseSpeed = Mathf.Lerp(START_SPEED, MAX_SPEED, Mathf.Clamp01(elapsed / RAMP_TIME));
        if (stun > 0f) { stun -= dt; baseSpeed *= 0.45f; }
        speed = Mathf.MoveTowards(speed, baseSpeed, 30f * dt);

        // ---- steering -> lateral position ----
        // steerInput +1 = press RIGHT = move rider to SCREEN-right. The camera looks down -Z, so
        // screen-right is world -X => world lateral velocity is the NEGATED input (don't invert controls).
        float steerAuth = (stun > 0f) ? 0.25f : 1f;
        float targetVx = -steerInput * 11f * steerAuth;
        steerVel = Mathf.MoveTowards(steerVel, targetVx, 60f * dt);
        playerX += steerVel * dt;
        playerX = Mathf.Clamp(playerX, -HALF_WIDTH + 0.6f, HALF_WIDTH - 0.6f);

        // ---- advance down the slope ----
        float newZ = player.position.z - speed * dt;

        // ---- air / jump arc ----
        if (Airborne)
        {
            airVel -= 26f * dt;
            airY += airVel * dt;
            if (airY <= 0f) { airY = 0f; airVel = 0f; OnLand(); }
        }
        float y = SurfaceY(newZ) + airY;
        player.position = new Vector3(playerX, y, newZ);

        // ---- bank the board into the turn; lean forward more at speed ----
        float roll = steerInput * 26f;     // +input => lean toward screen-right (into the turn)
        float pitch = 8f + Mathf.Clamp01(speed / MAX_SPEED) * 6f + (Airborne ? 10f : 0f);
        visual.localRotation = Quaternion.Slerp(visual.localRotation, Quaternion.Euler(pitch, -steerInput * 6f, roll), 1f - Mathf.Exp(-10f * dt));

        EmitTrail(dt);
        ProcessCourse();
        Generate();
        Cull();
        UpdateCamera(dt);
        UpdateMountains();
        TickHud(dt);

        if (showDbg) UpdateDbg();
    }

    void Generate()
    {
        while (genZ > player.position.z - HORIZON) GenerateRow();
    }

    void Cull()
    {
        float behind = player.position.z + DESPAWN;
        for (int i = obs.Count - 1; i >= 0; i--)
            if (obs[i].t == null || obs[i].t.position.z > behind) { if (obs[i].t) Destroy(obs[i].t.gameObject); obs.RemoveAt(i); }
        for (int i = gates.Count - 1; i >= 0; i--)
            if (gates[i].z > behind) { if (gates[i].L) Destroy(gates[i].L.gameObject); if (gates[i].R) Destroy(gates[i].R.gameObject); gates.RemoveAt(i); }
        for (int i = edges.Count - 1; i >= 0; i--)
            if (edges[i].z > behind) { if (edges[i].t) Destroy(edges[i].t.gameObject); edges.RemoveAt(i); }
    }

    // hit-test at the pass-line (dz=0 is the closest approach for a static object on a forward run)
    void ProcessCourse()
    {
        float pz = player.position.z;

        // gates: player z decreases downhill; a gate is "passed" once player z <= gate z
        foreach (var g in gates)
        {
            if (g.passed) continue;
            if (pz <= g.z)
            {
                g.passed = true; dbgPasses++;
                float dx = Mathf.Abs(playerX - g.x);
                if (dx < g.half) GatePass(g); else GateMiss(g);
            }
        }

        foreach (var o in obs)
        {
            if (o.passed) continue;
            if (pz <= o.z)
            {
                o.passed = true;
                float dx = Mathf.Abs(playerX - o.x);
                if (o.kind == 2) { if (dx < o.r && !Airborne) Launch(o); continue; }
                if (Airborne) continue;  // invincible while jumping
                if (dx < CRASH_DX + o.r * 0.4f) Crash(o);
                else if (dx < NEAR_DX + o.r * 0.4f) NearMiss(o);
            }
        }
    }

    // ===================================================================== events
    void GatePass(Gate g)
    {
        combo++;
        int gain = 100 + combo * 20;
        score += gain;
        comboFlash = 1f;
        if (combo >= 2) { combeText.text = "COMBO ×" + combo; FlashColor(); }
        Vector3 wp = new Vector3(g.x, SurfaceY(g.z) + 2f, g.z);
        Juice.Score(wp);
        Juice.Blip(740f + Mathf.Min(combo, 12) * 40f, 0.06f, 0.4f);
        // light the gate beam
        Pulse(g.x, g.z, new Color(1f, 0.95f, 0.4f));
        RefreshHud();
    }

    void GateMiss(Gate g)
    {
        if (combo >= 3) FloatText("COMBO LOST", new Color(0.8f, 0.85f, 0.95f));
        combo = 0; comboFlash = 0f; combeText.text = "";
    }

    void NearMiss(Ob o)
    {
        dbgNear++;
        score += 30;
        Juice.Blip(1180f, 0.05f, 0.25f);
        Juice.Pop(new Vector3(playerX, player.position.y + 0.6f, o.z), new Color(0.7f, 0.95f, 1f), 6);
        FloatText("+CLOSE!", new Color(0.7f, 0.95f, 1f));
        RefreshHud();
    }

    void Launch(Ob ramp)
    {
        airVel = 9.5f; airY = 0.001f;
        fovPunch = Mathf.Max(fovPunch, 7f);
        Juice.Blip(520f, 0.12f, 0.4f);
        FloatText("AIR!", new Color(1f, 0.9f, 0.3f));
        Juice.Shake(0.12f);
    }

    void OnLand()
    {
        int gain = 150;
        score += gain;
        Juice.Score(player.position + Vector3.up * 0.4f);
        Juice.Shake(0.18f);
        fovPunch = Mathf.Max(fovPunch, 4f);
        SpraySnow(22);
        FloatText("+" + gain, new Color(0.8f, 1f, 0.9f));
        RefreshHud();
    }

    void Crash(Ob o)
    {
        combo = 0; comboFlash = 0f; combeText.text = "";
        stun = 0.75f;
        speed *= 0.4f;
        lives--;
        Juice.Hit(); Juice.Shake(0.5f);
        Juice.Pop(player.position + Vector3.up * 0.8f, new Color(1f, 0.9f, 0.9f), 14);
        SpraySnow(18);
        fovPunch = Mathf.Max(fovPunch, -6f); // brief FOV pull-in
        if (lives <= 0) GameOver();
        else FloatText("WIPEOUT!", new Color(1f, 0.5f, 0.45f));
        RefreshHud();
    }

    void GameOver()
    {
        state = State.GameOver;
        bool nb = score > best;
        if (nb) { best = score; PlayerPrefs.SetInt("snowrush_best", best); PlayerPrefs.Save(); }
        Juice.Lose();
        // hide the in-run HUD so the result panel never collides with the SCORE/BEST row
        hudScore.gameObject.SetActive(false);
        hudBest.gameObject.SetActive(false);
        hudSpeed.gameObject.SetActive(false);
        combeText.gameObject.SetActive(false);
        bannerText.transform.localPosition = new Vector3(0f, 0f, HUD_Z);
        bannerText.characterSize = 0.092f * hudScale;   // sized so the longest line fits phone portrait
        bannerText.color = Color.white;
        bannerText.text = "RUN OVER\n\nSCORE  " + score + (nb ? "\nNEW BEST!" : "\nBEST  " + best) + "\n\nTAP TO RIDE AGAIN";
        bannerTimer = 9999f;
        RefreshHud();
    }

    void Restart()
    {
        foreach (var o in obs) if (o.t) Destroy(o.t.gameObject);
        foreach (var g in gates) { if (g.L) Destroy(g.L.gameObject); if (g.R) Destroy(g.R.gameObject); }
        foreach (var e in edges) if (e.t) Destroy(e.t.gameObject);
        obs.Clear(); gates.Clear(); edges.Clear();

        hudScore.gameObject.SetActive(true);
        hudBest.gameObject.SetActive(true);
        hudSpeed.gameObject.SetActive(true);
        combeText.gameObject.SetActive(true);
        state = State.Playing;
        playerX = 0; steerVel = 0; speed = START_SPEED; elapsed = 0;
        airY = 0; airVel = 0; stun = 0; fovPunch = 0;
        score = 0; combo = 0; lives = MAX_LIVES; comboFlash = 0;
        rowIndex = 0; genZ = -ROW;
        bannerText.text = ""; combeText.text = "";
        player.position = new Vector3(0, SurfaceY(0), 0);
        while (genZ > -HORIZON) GenerateRow();
        RefreshHud();
    }

    // ===================================================================== feel helpers
    void FlashColor()
    {
        combeText.color = combo >= 8 ? new Color(1f, 0.35f, 0.5f)
                        : combo >= 5 ? new Color(1f, 0.6f, 0.2f)
                                     : new Color(1f, 0.9f, 0.3f);
    }

    void FloatText(string s, Color c)
    {
        bannerText.transform.localPosition = new Vector3(0f, -halfH * 0.42f, HUD_Z);
        bannerText.characterSize = 0.15f * hudScale;
        bannerText.text = s; bannerText.color = c; bannerTimer = 0.6f;
    }

    void Pulse(float x, float z, Color c) { Juice.Pop(new Vector3(x, SurfaceY(z) + 2.4f, z), c, 8); }

    void SpraySnow(int n)
    {
        Juice.Pop(player.position + Vector3.up * 0.4f - player.forward * 0.5f, new Color(0.95f, 0.98f, 1f), n);
    }

    void EmitTrail(float dt)
    {
        if (Airborne || state != State.Playing) return;
        trailT += dt;
        float interval = Mathf.Lerp(0.06f, 0.02f, Mathf.Abs(steerInput));
        if (trailT < interval) return;
        trailT = 0f;
        var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
        var col = q.GetComponent<Collider>(); if (col) Destroy(col);
        q.transform.position = player.position + new Vector3(-steerInput * 0.4f, 0.18f, 0.9f);
        q.transform.rotation = Quaternion.Euler(90f, 0, 0);
        q.transform.localScale = Vector3.one * Random.Range(0.4f, 0.7f);
        var sh = Shader.Find("Sprites/Default"); if (sh == null) sh = Shader.Find("Unlit/Color");
        var mr = q.GetComponent<MeshRenderer>();
        mr.material = new Material(sh) { color = new Color(1f, 1f, 1f, 0.7f) };
        q.AddComponent<Puff>().Init(new Vector3(-steerInput * 2f + Random.Range(-1f,1f), 1.2f, 3.5f), mr);
    }

    // ===================================================================== camera / scenery
    void UpdateCamera(float dt)
    {
        if (cam == null || player == null) return;
        Vector3 p = player.position;
        Vector3 want = new Vector3(p.x * 0.55f, p.y + 4.5f, p.z + 8.2f);
        cam.position = Vector3.Lerp(cam.position, want, 1f - Mathf.Exp(-7f * dt));
        // look down the slope, ahead of the player
        float az = p.z - 16f;
        Vector3 look = new Vector3(p.x * 0.35f, SurfaceY(az) + 1.4f, az);
        Quaternion q = Quaternion.LookRotation(look - cam.position, Vector3.up);
        cam.rotation = Quaternion.Slerp(cam.rotation, q, 1f - Mathf.Exp(-9f * dt));

        // FOV: widen with speed (+ punch on boost/air), for a sense of rush
        fovPunch = Mathf.Lerp(fovPunch, 0f, 6f * dt);
        float baseFov = 60f + Mathf.Clamp01((speed - START_SPEED) / (MAX_SPEED - START_SPEED)) * 12f;
        camComp.fieldOfView = Mathf.Clamp(baseFov + fovPunch, 50f, 88f);
        AdjustHud();   // keep HUD anchored to the (speed/aspect-dependent) frustum every frame
    }

    void UpdateMountains()
    {
        foreach (var m in mountains)
        {
            if (m.t == null) continue;
            float mz = player.position.z - m.oz;
            m.t.position = new Vector3(m.ox, SurfaceY(mz) - 6f, mz);
        }
    }

    void TickHud(float dt)
    {
        // combo text scale punch
        if (comboFlash > 0f)
        {
            comboFlash -= dt * 2.2f;
            float s = 0.12f * hudScale * (1f + Mathf.Max(0f, comboFlash) * 0.6f);
            if (combeText) combeText.characterSize = s;
        }
        if (bannerTimer > 0f && bannerTimer < 9000f)
        {
            bannerTimer -= dt;
            if (bannerTimer <= 0f) { bannerText.text = ""; bannerText.color = Color.white; }
        }
        hudSpeed.text = Mathf.RoundToInt(speed * 3.6f) + " km/h";
    }

    void UpdateDbg()
    {
        float nd = 9999f;
        foreach (var o in obs) { float d = Mathf.Abs(o.t.position.z - player.position.z); if (d < nd) nd = d; }
        dbg.text = string.Format(
            "state {0}  spd {1:0.0}  steer {2:0.00}\nx {3:0.0}  z {4:0.0}  air {5:0.0}  stun {6:0.0}\nscore {7} combo {8} lives {9}\nobs {10} gates {11} edges {12}\npass {13} near {14}  fps {15:0}\nasp {16:0.00} scale {17:0.00} hW {18:0.0} fov {19:0}",
            state, speed, steerInput, playerX, player.position.z, airY, stun,
            score, combo, lives, obs.Count, gates.Count, edges.Count, dbgPasses, dbgNear, 1f / Mathf.Max(0.0001f, Time.smoothDeltaTime),
            camComp != null ? camComp.aspect : 0f, hudScale, halfW, camComp != null ? camComp.fieldOfView : 0f);
    }
}

// short-lived snow puff billboard kicked up by the board / crashes
public class Puff : MonoBehaviour
{
    Vector3 vel; MeshRenderer mr; float age, life = 0.55f; float baseA;
    public void Init(Vector3 v, MeshRenderer r) { vel = v; mr = r; baseA = r.material.color.a; }
    void Update()
    {
        float dt = Time.deltaTime; age += dt;
        vel.y -= 4f * dt;
        transform.position += vel * dt;
        transform.localScale *= 1f + dt * 1.8f;
        if (mr != null) { var c = mr.material.color; c.a = Mathf.Clamp01(baseA * (1f - age / life)); mr.material.color = c; }
        if (age >= life) Destroy(gameObject);
    }
}
