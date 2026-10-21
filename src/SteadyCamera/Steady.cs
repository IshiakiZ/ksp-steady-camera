using System;
using Keystone;
using UnityEngine;

#if !DEV
[assembly: KSPAssembly("SteadyCamera", 0, 1)]
[assembly: KSPAssemblyDependency("Keystone", 0, 4)]
#endif

namespace SteadyCamera
{
    /// <summary>
    /// The flight camera held steady while the craft spins.
    ///
    /// Asked for (2026-10-13): "A stable camera, because when your ship is rotating really, really fast, the camera shakes
    /// a bunch, and it's kind of unsettling."
    ///
    /// What was found in the game's own camera (FlightCamera.LateUpdate): after it has set the camera where it belongs, it
    /// turns it once more, every frame, by an angle as large as the craft's spin (in radians a second) times a setting
    /// (FLT_CAMERA_WOBBLE in settings.cfg, 0.1 as the game comes: a degree for a spin of ten radians a second), about the
    /// spin's own axis as the camera sees it. Meant as a little life in the picture; in a fast spin the angle is large and
    /// its axis swings round with the craft and jumps with every bump of the physics, and the view shakes.
    ///
    /// What this does: holds that turn to a share of the game's (none as installed), every frame (the game sets it back
    /// from its settings whenever the camera changes what it follows). The game's settings file is not touched: off, the
    /// camera is the game's own again.
    ///
    /// Tried (2026-10-13, development build, `Steady.Spin`, `HoldSpin`, `Drive`): a rocket tumbling in orbit at 350 deg/s,
    /// rolling at 1100 deg/s at 6 km, and driven through its own physics at 560 deg/s at 9 km, in the game's automatic
    /// view: frame by frame the view held steady with the wobble and without (the wobble is then one or two degrees,
    /// steady while the spin's axis is); the camera's pivot stayed within millimetres of the centre of mass. The shake the
    /// owner sees has not been made to happen here yet: which view (free, chase, locked, orbital) and which craft is still
    /// to be asked.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public sealed class SteadyAtStart : MonoBehaviour
    {
        void Awake() { Steady.Setup(); Destroy(gameObject); }
    }

    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public sealed class Steady : MonoBehaviour
    {
        public const string Version = "0.2.0";

        static Mod mod;
        static Toggle on, glide;
        static Slider keep, shakeOut, shakeIn;

        // (the glide to what the camera follows next: see Late)
        const float GlideTime = 0.6f, GlideMost = 150f;
        Transform followed;
        Vector3 lastPivot, glideOffset;
        float glideAge = -1f;

        // (what the readout says: how fast the craft spins, and how far the game would have turned the view for it)
        float spin, gameTurn, ownTurn;
#if DEV
        // (development build: how far the camera's pivot moved against the craft's centre of mass from one frame to the next.
        // A measure of the view's own turning frame to frame, tried first, did not match what the pictures showed and is gone.)
        static int shakeAt;
        Vector3 lastOff;
        static readonly float[] pivotJitter = new float[90];
        bool looked;
        static Steady last;
#endif

        /// <summary>The mod's page in Keystone's window: once, when the game starts.</summary>
        public static void Setup()
        {
            if (mod != null && Kit.Find("Steady Camera") == mod) return;
            mod = Kit.Register("Steady Camera", Version, "The flight camera held steady while the craft spins. The game turns the view a little more the faster the craft spins, about the spin's own axis: in a fast spin that shakes the picture. Your settings file is not touched.");
            on = mod.Toggle("on", "Steady camera", true, "Off: the camera is the game's own again, with its wobble.");
            keep = mod.Slider("keep", "Wobble kept", 0f, 0f, 1f, "", 2, "How much of the game's own wobble with the craft's spin is kept: 0 none, 1 all of it, as the game has it.");
            shakeOut = mod.Slider("shakeout", "Engines' shake kept", 1f, 0f, 1f, "", 2, "The game shakes the camera with its engines' rumble (solid boosters most) and the air's buffeting (its own camera effects, CAMERA_FX_EXTERNAL in its settings): how much of that is kept outside the craft. 1 as the game has it, 0 none. Your settings file is not touched.");
            shakeIn = mod.Slider("shakein", "In the cockpit", 1f, 0f, 1f, "", 2, "The same in the cockpit view (CAMERA_FX_INTERNAL), where it sways the view against the instruments. 1 as the game has it, 0 none.");
            glide = mod.Toggle("glide", "Glide to what it follows next", true, "When the camera changes what it follows (a kerbal stepping out, another craft near by taken over, a part aimed at), the game puts it there at once and the picture jumps. With this on it glides there over a moment instead (not over more than 150 m: further, it still goes at once).");
        }

        void Awake()
        {
            Setup();
            mod.Panel = Readout;
            TimingManager.LateUpdateAdd(TimingManager.TimingStage.BetterLateThanNever, Late);
            TimingManager.UpdateAdd(TimingManager.TimingStage.ObscenelyEarly, HoldShake);
            GameEvents.OnGameSettingsWritten.Add(SettingsWritten);
            Camera.onPreCull += BeforeDrawing;
            Camera.onPostRender += AfterDrawing;
#if DEV
            last = this;
#endif
        }

        void OnDestroy()
        {
            TimingManager.LateUpdateRemove(TimingManager.TimingStage.BetterLateThanNever, Late);
            TimingManager.UpdateRemove(TimingManager.TimingStage.ObscenelyEarly, HoldShake);
            GameEvents.OnGameSettingsWritten.Remove(SettingsWritten);
            GiveShake();
            Camera.onPreCull -= BeforeDrawing;
            Camera.onPostRender -= AfterDrawing;
            if (moved != null) { moved.localPosition = movedWas; moved = null; }
            if (FlightCamera.fetch != null) FlightCamera.fetch.cameraWobbleSensitivity = GameSettings.FLT_CAMERA_WOBBLE;
            if (mod != null && mod.Panel == (Action)Readout) mod.Panel = null;
        }

        void Update()
        {
            FlightCamera cam = FlightCamera.fetch;
            if (cam == null) return;
            // (every frame, before the game's camera moves in its LateUpdate: it sets this back whenever it changes what it follows)
            cam.cameraWobbleSensitivity = on ? GameSettings.FLT_CAMERA_WOBBLE * Mathf.Clamp01(keep) : GameSettings.FLT_CAMERA_WOBBLE;
            Vessel v = FlightGlobals.ActiveVessel;
            Rigidbody rb = v != null && v.rootPart != null ? v.rootPart.rb : null;
            spin = rb != null ? rb.angularVelocity.magnitude : 0f;
            gameTurn = spin * GameSettings.FLT_CAMERA_WOBBLE;
            ownTurn = spin * cam.cameraWobbleSensitivity;
        }

        // (since 0.2.0: the game's own camera shake held to a share of it: the game's values while held, and what was set)
        float shakeOutWas = float.NaN, shakeInWas, shakeOutSet, shakeInSet;
        bool writtenWhileHeld;

        /// <summary>
        /// (Since 0.2.0.) The game's own camera shake (its CameraFX: the engines' rumble by their kind, a solid booster's most, and
        /// the air's buffeting, which shake the camera outside and sway the cockpit view against its instruments), held to the share
        /// set. The game reads how much it wants (its settings CAMERA_FX_EXTERNAL and CAMERA_FX_INTERNAL, 1 as it comes) as its
        /// cameras move, the cockpit's in the frame's Update and the outside one's in its LateUpdate: the share is set at the very
        /// start of the frame's Update and the game's own values given back once both have moved (in Late), every frame. The game's
        /// settings never keep it: not set while its pause menu is open (where its settings are changed); if they are changed while
        /// it is held, theirs stand; and if the game writes its settings file while it is held, the file is written again with the
        /// game's own values as soon as they are back, the same frame.
        /// </summary>
        void HoldShake()
        {
            GiveShake();
            if (!on || (PauseMenu.exists && PauseMenu.isOpen) || (shakeOut.Value >= 0.999f && shakeIn.Value >= 0.999f)) return;
            shakeOutWas = GameSettings.CAMERA_FX_EXTERNAL; shakeInWas = GameSettings.CAMERA_FX_INTERNAL;
            shakeOutSet = GameSettings.CAMERA_FX_EXTERNAL = shakeOutWas * Mathf.Clamp01(shakeOut.Value);
            shakeInSet = GameSettings.CAMERA_FX_INTERNAL = shakeInWas * Mathf.Clamp01(shakeIn.Value);
        }

        void GiveShake()
        {
            if (float.IsNaN(shakeOutWas)) return;
            if (GameSettings.CAMERA_FX_EXTERNAL == shakeOutSet) GameSettings.CAMERA_FX_EXTERNAL = shakeOutWas;
            if (GameSettings.CAMERA_FX_INTERNAL == shakeInSet) GameSettings.CAMERA_FX_INTERNAL = shakeInWas;
            shakeOutWas = float.NaN;
            if (writtenWhileHeld) { writtenWhileHeld = false; GameSettings.SaveGameSettingsOnly(); }
        }

        void SettingsWritten() { if (!float.IsNaN(shakeOutWas)) writtenWhileHeld = true; }

        /// <summary>
        /// After the game's camera has been placed (the last of the game's late stages): whether it has begun to follow something
        /// else, which the game makes happen at once (FlightCamera.SetTarget puts the camera's pivot on its new target), the picture
        /// jumping by however far the camera moved. If it has, the camera is drawn from where it was and glides to its new place
        /// over a moment: only while each frame is drawn (BeforeDrawing, AfterDrawing), never moved for the game itself, which eases
        /// the camera towards its place every frame and would carry an offset left on it on and on (the first try, moving the pivot,
        /// put the camera under the launch pad when a kerbal stepped out: a kerbal's pivot is his own, and nothing sets it back).
        /// </summary>
        void Late()
        {
            GiveShake();
            FlightCamera cam = FlightCamera.fetch;
            if (cam == null) { followed = null; glideAge = -1f; return; }
            Vector3 at = cam.transform.position;
            Transform now = cam.Target;
            if (now != followed)
            {
                Vector3 jump = lastPivot - at;
                if (followed != null && on && glide && jump.magnitude > 0.05f && jump.magnitude < GlideMost) { glideOffset = jump; glideAge = 0f; lastGlide = jump.magnitude; }
                followed = now;
            }
            else if (glideAge >= 0f)
            {
                glideAge += Mathf.Min(Time.unscaledDeltaTime, 0.1f);
                if (glideAge >= GlideTime || !on || !glide) glideAge = -1f;
            }
            lastPivot = at;
        }

        // (the camera as the game placed it, while it is drawn from along the glide)
        Transform moved;
        Vector3 movedWas;
        int drawnFrame = -1;

        void BeforeDrawing(Camera c)
        {
            if (glideAge < 0f || drawnFrame == Time.frameCount || c.targetTexture != null) return;
            FlightCamera cam = FlightCamera.fetch;
            if (cam == null) return;
            drawnFrame = Time.frameCount;
            moved = cam.transform; movedWas = moved.localPosition;
            moved.position += glideOffset * (1f - Mathf.SmoothStep(0f, 1f, glideAge / GlideTime));
        }

        void AfterDrawing(Camera c)
        {
            if (moved == null || FlightCamera.fetch == null || c != FlightCamera.fetch.mainCamera) return;
            moved.localPosition = movedWas;
            moved = null;
        }

#if DEV
        void LateUpdate()
        {
            // (after the game's camera has moved: how far the view turned since the last frame)
            Camera c = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : null;
            if (c == null) return;
            // (where the camera's pivot is against the craft's true centre of mass, frame to frame: a pivot riding a part that
            // flexes and jitters moves the whole view with it)
            Vessel v = FlightGlobals.ActiveVessel;
            if (v != null && FlightCamera.fetch.GetPivot() != null)
            {
                Vector3 off = FlightCamera.fetch.GetPivot().position - (Vector3)v.CoMD;
                pivotJitter[shakeAt] = looked ? (off - lastOff).magnitude : 0f;
                lastOff = off;
            }
            shakeAt = (shakeAt + 1) % pivotJitter.Length;
            looked = true;
        }

        /// <summary>(Development build) the camera made to follow another thing at once: "first" or "last" part of the craft, or "craft" (its centre of mass), as the game's "aim camera" does.</summary>
        public static string Aim(string which)
        {
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null || v.parts.Count == 0) return "no craft";
            string w = (which ?? "").Trim();
            if (w == "craft") FlightCamera.SetTarget(v);
            else FlightCamera.SetTarget(w == "last" ? v.parts[v.parts.Count - 1] : v.parts[0]);
            return "the camera follows " + (w == "craft" ? "the craft" : (w == "last" ? v.parts[v.parts.Count - 1] : v.parts[0]).partInfo.title);
        }

        /// <summary>(Development build) the flight camera's mode: "free", "orbital", "chase", "locked" or "auto"; nothing says which it is.</summary>
        public static string CameraMode(string arg)
        {
            if (FlightCamera.fetch == null) return "no flight camera";
            if (!string.IsNullOrEmpty(arg) && Enum.TryParse(arg.Trim(), true, out FlightCamera.Modes m)) FlightCamera.SetModeImmediate(m);
            return "the camera is " + FlightCamera.fetch.mode;
        }

        static string heldSpin = "";
        /// <summary>(Development build) keep the craft spinning so: "x y z" as for Spin, set again every physics step; "off" lets it be.</summary>
        public static string HoldSpin(string arg) { heldSpin = arg == null || arg.Trim() == "off" ? "" : arg; return heldSpin.Length > 0 ? "held spinning: " + heldSpin : "let go"; }
        static Vector3 drive;
        /// <summary>(Development build) drive the craft's spin through the physics: "x y z" radians a second squared about its own axes, given to every part each step (its joints then carry the load and flex, as under a real torque); "off".</summary>
        public static string Drive(string arg)
        {
            string[] t = (arg ?? "").Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
            float V(int i) => i < t.Length && float.TryParse(t[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float x) ? x : 0f;
            drive = arg == null || arg.Trim() == "off" ? Vector3.zero : new Vector3(V(0), V(1), V(2));
            return drive == Vector3.zero ? "let go" : "driving " + drive;
        }
        void FixedUpdate()
        {
            if (heldSpin.Length > 0) Spin(heldSpin);
            Vessel v = FlightGlobals.ActiveVessel;
            if (drive != Vector3.zero && v != null && !v.packed)
            {
                Transform r = v.ReferenceTransform;
                Vector3 a = r.right * drive.x + r.up * drive.y + r.forward * drive.z;
                foreach (Part p in v.parts) if (p != null && p.rb != null) { p.rb.maxAngularVelocity = 200f; p.rb.AddTorque(a, ForceMode.Acceleration); }
            }
        }

        /// <summary>(Development build) the craft being flown set spinning: "x y z" in radians a second about its own axes (each part moved as a rigid body turning about the craft's centre of mass would be).</summary>
        public static string Spin(string arg)
        {
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null || v.packed) return "no craft, or packed";
            string[] t = (arg ?? "").Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
            float V(int i) => i < t.Length && float.TryParse(t[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float x) ? x : 0f;
            Transform r = v.ReferenceTransform;
            Vector3 w = r.right * V(0) + r.up * V(1) + r.forward * V(2);
            Vector3 com = (Vector3)v.CoMD;
            Vector3 along = v.rootPart != null && v.rootPart.rb != null ? v.rootPart.rb.velocity - Vector3.Cross(v.rootPart.rb.angularVelocity, v.rootPart.rb.worldCenterOfMass - com) : Vector3.zero;
            foreach (Part p in v.parts)
            {
                if (p == null || p.rb == null) continue;
                p.rb.maxAngularVelocity = Mathf.Max(p.rb.maxAngularVelocity, w.magnitude * 2f);
                p.rb.angularVelocity = w;
                p.rb.velocity = along + Vector3.Cross(w, p.rb.worldCenterOfMass - com);
            }
            return "set spinning at " + (w.magnitude * Mathf.Rad2Deg).ToString("F0") + " deg/s";
        }

        /// <summary>(Development build) how fast the craft spins, the game's wobble for it and what is kept, and how much the view turned frame to frame lately.</summary>
        public static string Says()
        {
            Steady s = last;
            if (s == null) return "not in flight";
            float psum = 0f, pmost = 0f;
            for (int i = 0; i < pivotJitter.Length; i++) { psum += pivotJitter[i]; pmost = Mathf.Max(pmost, pivotJitter[i]); }
            return "camera " + (FlightCamera.fetch != null ? FlightCamera.fetch.mode.ToString() : "none") + "; spin " + (s.spin * Mathf.Rad2Deg).ToString("F0") + " deg/s; the game's wobble " + s.gameTurn.ToString("F2") + " deg, kept " + s.ownTurn.ToString("F2")
                 + " deg; the pivot against the centre of mass moved " + (psum / pivotJitter.Length).ToString("F3") + " m a frame on the mean, at most " + pmost.ToString("F3") + ", over the last " + pivotJitter.Length + " frames"
                 + "; the game's shake " + (float.IsNaN(s.shakeOutWas) ? "as it has it: " + GameSettings.CAMERA_FX_EXTERNAL.ToString("F2") + " outside, " + GameSettings.CAMERA_FX_INTERNAL.ToString("F2") + " in the cockpit"
                    : "held to " + s.shakeOutSet.ToString("F2") + " of its " + s.shakeOutWas.ToString("F2") + " outside, " + s.shakeInSet.ToString("F2") + " of its " + s.shakeInWas.ToString("F2") + " in the cockpit");
        }
#endif

        void Readout()
        {
            if (!HighLogic.LoadedSceneIsFlight) return;
            GUILayout.Label("The craft spins at " + (spin * Mathf.Rad2Deg).ToString("F0") + " degrees a second: the game would turn the view by " + gameTurn.ToString("F1") + " degrees for it" + (on ? "; held to " + ownTurn.ToString("F1") + "." : "."), Host.Small);
            GUILayout.Label("The game's wobble is a turn of the view, about the spin's own axis, as large as the spin times " + GameSettings.FLT_CAMERA_WOBBLE.ToString("F2") + " (its FLT_CAMERA_WOBBLE): in a fast spin it swings round with the craft and the picture shakes. " +
                            (glide && on ? (lastGlide > 0f ? "The last change of what the camera follows: glided " + lastGlide.ToString("F1") + " m." : "No change of what the camera follows yet.") : "Changes of what the camera follows: at once, as the game has them."), Host.Small);
        }
        float lastGlide;
    }
}
