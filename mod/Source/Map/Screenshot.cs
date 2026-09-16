using System;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using RimBridge.Server;
using UnityEngine;
using Verse;

namespace RimBridge.MapView
{
    /// <summary>
    /// Off-screen capture that does not touch the player's camera. RimWorld only draws map sections and dynamic
    /// things inside CameraDriver.CurrentViewRect, so for the capture frame we widen that rect (Harmony postfix)
    /// to include the capture region, and let a second, enabled camera render into a RenderTexture through the
    /// normal pipeline. The following frame we read the pixels back and disable the camera again.
    /// </summary>
    [StaticConstructorOnStartup]
    public static class Screenshot
    {
        public static CellRect? PendingRect;
        static Camera? _cam;
        static RenderTexture? _rt;

        static Camera GetCamera(int w, int h)
        {
            if (_cam == null)
            {
                var go = new GameObject("RimBridgeCaptureCamera");
                UnityEngine.Object.DontDestroyOnLoad(go);
                _cam = go.AddComponent<Camera>();
                _cam.enabled = false;
            }
            if (_rt == null || _rt.width != w || _rt.height != h)
            {
                if (_rt != null) { _rt.Release(); UnityEngine.Object.Destroy(_rt); }
                _rt = new RenderTexture(w, h, 24);
            }
            return _cam;
        }

        // Runs on the request thread and schedules two main-thread jobs (setup now, read-back two frames later).
        [Rpc("map.screenshot_bytes", "{x?, z?, w?: cells wide (default 60), width_px?: 1024, height_px?: 768} render the map around [x,z] to PNG (base64). Also GET /screenshot?x=&z=&w=. Does not move the player's camera.", MainThread = false)]
        public static JToken Bytes(JObject p)
        {
            int wpx = Mathf.Clamp(P.Int(p, "width_px", 1024), 128, 2048), hpx = Mathf.Clamp(P.Int(p, "height_px", 768), 128, 2048);
            Camera cam = null!;
            MainThreadQueue.Run(() =>
            {
                GameCtl.GameControl.RequirePlaying();
                var map = Find.CurrentMap;
                var home = State.Snapshot.HomeCenter(map);
                int x = P.Int(p, "x", home.x), z = P.Int(p, "z", home.z);
                float cellsWide = Mathf.Clamp(P.Float(p, "w", 60f), 10f, 250f);
                float aspect = (float)wpx / hpx;
                float ortho = cellsWide / (2f * aspect);
                var main = Find.Camera;
                cam = GetCamera(wpx, hpx);
                cam.CopyFrom(main);
                cam.targetTexture = _rt;
                cam.orthographic = true;
                cam.orthographicSize = ortho;
                cam.transform.position = new Vector3(x + 0.5f, main.transform.position.y, z + 0.5f);
                cam.transform.rotation = main.transform.rotation;
                cam.depth = main.depth - 1;
                cam.enabled = true;
                PendingRect = CellRect.FromLimits(Mathf.FloorToInt(x - ortho * aspect - 1), Mathf.FloorToInt(z - ortho - 1), Mathf.CeilToInt(x + ortho * aspect + 1), Mathf.CeilToInt(z + ortho + 1)).ClipInsideMap(map);
                return null;
            });
            // Frame N: camera enabled, view rect widened -> Unity renders it this frame. Frame N+2: read back.
            var result = MainThreadQueue.Run(() =>
            {
                try
                {
                    var prevActive = RenderTexture.active;
                    RenderTexture.active = _rt;
                    var tex = new Texture2D(wpx, hpx, TextureFormat.RGB24, false);
                    tex.ReadPixels(new Rect(0, 0, wpx, hpx), 0, 0);
                    tex.Apply();
                    RenderTexture.active = prevActive;
                    var png = tex.EncodeToPNG();
                    UnityEngine.Object.Destroy(tex);
                    return (JToken)Convert.ToBase64String(png);
                }
                finally
                {
                    cam.enabled = false;
                    cam.targetTexture = null;
                    PendingRect = null;
                }
            }, 10000, delayFrames: 2);
            return result!;
        }
    }

    [HarmonyPatch(typeof(CameraDriver), nameof(CameraDriver.CurrentViewRect), MethodType.Getter)]
    static class Patch_ViewRect
    {
        static void Postfix(ref CellRect __result)
        {
            var pr = Screenshot.PendingRect;
            if (pr.HasValue) __result = __result.Encapsulate(pr.Value);
        }
    }
}
