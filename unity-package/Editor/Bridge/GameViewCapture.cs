using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Gamebrew.Bridge
{
    /// <summary>
    /// EDITOR-ONLY. Shared off-screen render-to-PNG helper used by PlayViewCoordinator
    /// (and available to any future coordinator that needs a headless screen capture).
    ///
    /// This is the SINGLE shared capture body (D1 collapse): CommandRouter.HandleCaptureGameView
    /// and play.minigame.captureHud both delegate here, so the live-HUD pump + render live in one
    /// place. Callers wrap the call in MainThreadDispatcher.Run (Capture has no Run of its own).
    ///
    /// Algorithm:
    ///   pump live HUD (GameHudController.Active.DriveHudVisuals(force:true), editor-only)
    ///   → flip ScreenSpaceOverlay canvases to ScreenSpaceCamera
    ///   → RenderPipeline.SubmitRenderRequest (URP) or targetTexture + QueueUpdate
    ///   → ReadPixels → EncodeToPNG → write file → restore canvas modes.
    /// </summary>
    public static class GameViewCapture
    {
        public const int DefaultWidth = 1280;
        public const int DefaultHeight = 720;

        public readonly struct Result
        {
            public readonly bool Success;
            public readonly string Error;
            public readonly string FullPath;
            public readonly long Bytes;

            public Result(string fullPath, long bytes)
            {
                Success = true;
                Error = null;
                FullPath = fullPath;
                Bytes = bytes;
            }

            public Result(string error)
            {
                Success = false;
                Error = error;
                FullPath = null;
                Bytes = 0;
            }
        }

        /// <summary>
        /// Capture the main camera off-screen and write a PNG to <paramref name="relativePath"/>
        /// (relative to the project root). Must be called on the main thread (use
        /// <see cref="MainThreadDispatcher.Run"/> from background threads).
        ///
        /// Returns a <see cref="Result"/> with Success=true and the written byte count on
        /// success, or Success=false and an Error string on failure.
        /// </summary>
        /// <param name="relativePath">
        /// Project-relative path, e.g. "Logs/view-MainCamera.png". Must not start with "/"
        /// or contain "..". The directory is created if absent.
        /// </param>
        public static Result Capture(string relativePath)
        {
            // Resolve the scene's main camera and delegate to the shared body. This keeps the
            // single render path (HUD pump + canvas-flip + SubmitRenderRequest) in one place.
            var cam = Camera.main ?? UnityEngine.Object.FindAnyObjectByType<Camera>();
            if (cam == null)
                return new Result("No camera found in the scene");

            return CaptureFromCamera(cam, relativePath);
        }

        /// <summary>
        /// Capture an EXPLICIT camera off-screen and write a PNG to <paramref name="relativePath"/>
        /// (relative to the project root). Must be called on the main thread (use
        /// <see cref="MainThreadDispatcher.Run"/> from background threads).
        ///
        /// This is the single shared render body; <see cref="Capture"/> delegates here with the
        /// scene's main camera. Coordinators that render a transient/off-axis camera
        /// (e.g. play.orbitView) pass their own camera in. The caller owns the camera's
        /// lifetime — this method neither creates nor destroys it; it only renders it.
        /// </summary>
        /// <param name="cam">Camera to render. Must be non-null and not destroyed.</param>
        /// <param name="relativePath">
        /// Project-relative path, e.g. "Logs/orbit.png". Must not start with "/" or contain
        /// "..". The directory is created if absent.
        /// </param>
        public static Result CaptureFromCamera(Camera cam, string relativePath)
        {
            // ── path validation ────────────────────────────────────────────
            if (string.IsNullOrWhiteSpace(relativePath))
                return new Result("path must not be empty");

            if (relativePath.StartsWith("/", StringComparison.Ordinal))
                return new Result("path must be relative to the project root (must not start with '/')");

            if (relativePath.Contains("..", StringComparison.Ordinal))
                return new Result("path must not contain '..'");

            // ── camera validation ──────────────────────────────────────────
            // Unity's overloaded == treats a destroyed Camera as null, so this guards both.
            if (cam == null)
                return new Result("camera is null or destroyed");

            string fullPath = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), relativePath));
            string dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            // ── off-screen render ──────────────────────────────────────────
            const int W = DefaultWidth;
            const int H = DefaultHeight;

            var rt = RenderTexture.GetTemporary(W, H, 24, RenderTextureFormat.ARGB32);
            var prevTarget = cam.targetTexture;
            var prevActive = RenderTexture.active;

            // Pump the per-frame HUD visual refresh ONCE on the main thread (this body runs
            // on the caller's guaranteed-main thread — no Run of its own) IMMEDIATELY before
            // the canvas-flip enumeration below, so any widget Update would have toggled
            // (e.g. the Pour strip's PourStripRoot.SetActive) is applied BEFORE we snapshot
            // canvases. This guarantees the now-active strip's canvas is enumerated and
            // flipped into the composited render target (the §3.5 ordering invariant). The
            // forced refresh resets the overlay label caches so it can't no-op a stale label.
#if UNITY_EDITOR
            GameHudController.Active?.DriveHudVisuals(force: true);
#endif

            // Flip ScreenSpaceOverlay canvases so the HUD composites into the RT.
            var canvases = UnityEngine.Object.FindObjectsByType<Canvas>(FindObjectsSortMode.None);
            var flipped = new List<(Canvas c, RenderMode mode, Camera worldCam, float plane)>();
            foreach (var canvas in canvases)
            {
                if (canvas.isActiveAndEnabled && canvas.renderMode == RenderMode.ScreenSpaceOverlay)
                {
                    flipped.Add((canvas, canvas.renderMode, canvas.worldCamera, canvas.planeDistance));
                    canvas.renderMode = RenderMode.ScreenSpaceCamera;
                    canvas.worldCamera = cam;
                    canvas.planeDistance = 0.1f;
                }
            }

            long bytes = 0;
            string captureError = null;

            try
            {
                // URP: SubmitRenderRequest renders synchronously into rt.
                // Fallback for non-URP: assign targetTexture and queue a player-loop update.
                var request = new RenderPipeline.StandardRequest { destination = rt };
                if (RenderPipeline.SupportsRenderRequest(cam, request))
                    RenderPipeline.SubmitRenderRequest(cam, request);
                else
                {
                    cam.targetTexture = rt;
                    EditorApplication.QueuePlayerLoopUpdate();
                }

                RenderTexture.active = rt;
                var tex = new Texture2D(W, H, TextureFormat.RGB24, false);
                try
                {
                    tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
                    tex.Apply();
                    byte[] png = tex.EncodeToPNG();
                    File.WriteAllBytes(fullPath, png);
                    bytes = png.Length;
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(tex);
                }
            }
            catch (Exception ex)
            {
                captureError = ex.Message;
            }
            finally
            {
                // Always restore canvas modes before releasing the RT.
                foreach (var (canvas, mode, worldCam, plane) in flipped)
                {
                    canvas.renderMode = mode;
                    canvas.worldCamera = worldCam;
                    canvas.planeDistance = plane;
                }

                cam.targetTexture = prevTarget;
                RenderTexture.active = prevActive;
                RenderTexture.ReleaseTemporary(rt);
            }

            return captureError != null
                ? new Result(captureError)
                : new Result(fullPath, bytes);
        }

        /// <summary>
        /// Pure spherical-offset helper for orbit-style framing (play.orbitView).
        ///
        /// CONVENTION: <paramref name="yaw"/> rotates around world Y (0 = camera behind the
        /// target on −Z); <paramref name="pitch"/> raises the camera above the horizon
        /// (0 = horizontal/eye-level, +90 = directly above looking straight DOWN).
        ///
        /// offset = Quaternion.Euler(pitch, yaw, 0) * (Vector3.back * distance)
        ///
        /// So the camera world position is <c>center + SphericalOffset(...)</c> and aiming it
        /// at <c>center</c> (transform.LookAt) yields a view from that pitch/yaw. Because the
        /// offset is built from Vector3.back, pitch&gt;0 places the camera HIGH and the
        /// LookAt(center) tilts it DOWN onto the target (e.g. pitch=90 → top-down).
        /// distance is the straight-line camera→center separation in world units.
        /// </summary>
        public static Vector3 SphericalOffset(float pitch, float yaw, float distance)
        {
            return Quaternion.Euler(pitch, yaw, 0f) * (Vector3.back * distance);
        }

        /// <summary>
        /// Sanitise a target string into a safe filename fragment by replacing
        /// slashes, spaces, and other path-unsafe characters with hyphens.
        /// e.g. "Player/Camera" → "Player-Camera"
        /// </summary>
        public static string SanitiseForFilename(string target)
        {
            if (string.IsNullOrWhiteSpace(target))
                return "unknown";

            // Replace any character that is illegal in filenames or path-dangerous.
            var sb = new System.Text.StringBuilder(target.Length);
            foreach (char c in target)
            {
                if (c == '/' || c == '\\' || c == ':' || c == '*' || c == '?' ||
                    c == '"' || c == '<' || c == '>' || c == '|' || c == ' ')
                    sb.Append('-');
                else
                    sb.Append(c);
            }

            return sb.ToString();
        }
    }
}
