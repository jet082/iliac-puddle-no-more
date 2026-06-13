// Project:         Iliac Puddle No More
// License:         MIT

using System.Text;
using UnityEngine;

namespace DeepWaters
{
    /// <summary>
    /// Lightweight always-on frame-cost probe. The mod's per-frame entry
    /// points wrap themselves in Begin/End; one summary line is logged every
    /// ReportIntervalSeconds while underwater presentation is active (or any
    /// time the average frame cost is high), attributing per-frame
    /// milliseconds to each subsystem. The residual between the frame time
    /// and the instrumented sections is DFU/engine/GPU work the mod does not
    /// own. No per-frame allocations outside the report line itself.
    /// </summary>
    internal static class DeepWaterPerf
    {
        public const int Driver = 0;
        public const int Gate = 1;
        public const int DriverFixed = 2;
        public const int After = 3;
        public const int Mover = 4;
        public const int Fog = 5;
        public const int Fish = 6;
        private const int SectionCount = 7;

        private static readonly string[] SectionNames =
        {
            "swimDriver", "colliderGate", "swimFixed", "swimAfter",
            "swimMover", "fogDriver", "fish",
        };

        private const float ReportIntervalSeconds = 10f;
        private const float AlwaysReportAboveAverageFrameMs = 30f;

        private static readonly long[] sectionTicks = new long[SectionCount];
        private static readonly int[] sectionCalls = new int[SectionCount];
        private static readonly long[] sectionOpenedAt = new long[SectionCount];
        private static readonly StringBuilder reportBuilder = new StringBuilder(512);

        private static float windowSeconds;
        private static float maxFrameSeconds;
        private static int windowFrames;
        private static bool sawUnderwater;
        private static float nextReportTime;

        public static void Begin(int section)
        {
            sectionOpenedAt[section] = System.Diagnostics.Stopwatch.GetTimestamp();
        }

        public static void End(int section)
        {
            sectionTicks[section] += System.Diagnostics.Stopwatch.GetTimestamp() - sectionOpenedAt[section];
            sectionCalls[section]++;
        }

        public static void NoteUnderwater()
        {
            sawUnderwater = true;
        }

        /// <summary>
        /// Called exactly once per frame (from the fog driver's LateUpdate,
        /// which lives permanently on the mod object).
        /// </summary>
        public static void Tick()
        {
            float dt = Time.unscaledDeltaTime;
            windowSeconds += dt;
            if (dt > maxFrameSeconds)
                maxFrameSeconds = dt;
            windowFrames++;

            if (Time.unscaledTime < nextReportTime)
                return;

            nextReportTime = Time.unscaledTime + ReportIntervalSeconds;

            if (windowFrames > 0)
            {
                float averageFrameMs = windowSeconds * 1000f / windowFrames;
                if (sawUnderwater || averageFrameMs > AlwaysReportAboveAverageFrameMs)
                    LogReport(averageFrameMs);
            }

            ResetWindow();
        }

        private static void LogReport(float averageFrameMs)
        {
            double ticksToMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;

            reportBuilder.Length = 0;
            reportBuilder.Append("[DeepWaters.Perf] avgFrame=").Append(averageFrameMs.ToString("F1"))
                .Append("ms maxFrame=").Append((maxFrameSeconds * 1000f).ToString("F0"))
                .Append("ms frames=").Append(windowFrames)
                .Append(" underwater=").Append(sawUnderwater)
                .Append(" fish=").Append(UnderwaterPassiveFishSpawner.LiveFishCount)
                .Append(" | per-frame ms:");

            double instrumentedMs = 0.0;
            for (int i = 0; i < SectionCount; i++)
            {
                double msPerFrame = sectionTicks[i] * ticksToMs / windowFrames;
                instrumentedMs += msPerFrame;
                reportBuilder.Append(' ').Append(SectionNames[i]).Append('=')
                    .Append(msPerFrame.ToString("F2"));
                if (sectionCalls[i] > windowFrames)
                    reportBuilder.Append("(x").Append((sectionCalls[i] / (float)windowFrames).ToString("F0")).Append(')');
            }

            reportBuilder.Append(" | modTotal=").Append(instrumentedMs.ToString("F2"))
                .Append(" other=").Append(Mathf.Max(0f, averageFrameMs - (float)instrumentedMs).ToString("F1"))
                .Append(" (other = DFU/engine/GPU/render — not instrumented)");

            Debug.Log(reportBuilder.ToString());
        }

        private static void ResetWindow()
        {
            for (int i = 0; i < SectionCount; i++)
            {
                sectionTicks[i] = 0;
                sectionCalls[i] = 0;
            }

            windowSeconds = 0f;
            maxFrameSeconds = 0f;
            windowFrames = 0;
            sawUnderwater = false;
        }
    }
}
