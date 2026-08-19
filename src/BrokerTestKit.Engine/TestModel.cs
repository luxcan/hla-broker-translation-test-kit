using System;
using System.Collections.Generic;
using System.Globalization;

namespace BrokerTestKit.Engine
{
    /// <summary>One attribute cell from a CSV row.</summary>
    public sealed class AttributeCell
    {
        /// <summary>Attribute name. Dotted paths address sub-fields, e.g. "worldPosition.x".</summary>
        public string Name;
        /// <summary>Raw text from the CSV (interpreted per <see cref="DataType"/>).</summary>
        public string Value;
        /// <summary>Only required for CUSTOM attributes VR-Link does not model (e.g. "hex").</summary>
        public string DataType;
        /// <summary>Per-field tolerance for numeric compares. Null = exact.</summary>
        public double? Tolerance;

        public override string ToString() { return Name + "=" + Value; }
    }

    /// <summary>
    /// One stimulus step: everything published to Federation A at the same time.
    /// Steps are processed strictly in order (DESIGN 9.1 app-level serialization).
    /// </summary>
    public sealed class InputStep
    {
        public int Step;
        public string Action = "publish";
        public string ObjClass = "BaseEntity";
        public string EntityId;
        public string Marking;
        public readonly List<AttributeCell> Attributes = new List<AttributeCell>();
    }

    /// <summary>What Federation B must show after the matching input step.</summary>
    public sealed class ExpectedStep
    {
        public int Step;
        public string ObjClass = "BaseEntity";
        public string EntityId;
        public readonly List<AttributeCell> Expected = new List<AttributeCell>();
    }

    public enum Outcome { Pass, Fail, Timeout, Skipped }

    /// <summary>Result of comparing one expected attribute.</summary>
    public sealed class FieldResult
    {
        public string Name;
        public string Expected;
        public string Actual;
        public bool Ok;
        public string Note;

        public override string ToString()
        {
            return string.Format("{0} {1}: expected={2} actual={3}{4}",
                Ok ? "OK  " : "FAIL", Name, Expected, Actual,
                string.IsNullOrEmpty(Note) ? "" : "  (" + Note + ")");
        }
    }

    public sealed class StepResult
    {
        public int Step;
        public Outcome Outcome;
        /// <summary>Whole step: quiesce + publish + wait. What the operator waits for.</summary>
        public TimeSpan Elapsed;

        /// <summary>
        /// Send to receive: from the instant the update was handed to federation A to the instant
        /// the matching state was observed on federation B.
        ///
        /// Deliberately not <see cref="Elapsed"/>. That one also carries the inter-step quiesce and
        /// every SETATTR round-trip, so it says how fast the HARNESS runs, not how fast the bridge
        /// is. Measured off the arrival timestamp the reader thread stamps on the reflect as the
        /// line comes off the agent, so the 25 ms match-polling interval does not round it.
        ///
        /// It is an UPPER BOUND on the broker: agent A's own dispatch sits inside it. On this rig
        /// the reflect on B routinely arrives before agent A has even printed its "OK UPDATE", so
        /// the bridge itself is faster than the number here.
        ///
        /// Null when nothing arrived.
        /// </summary>
        public TimeSpan? Latency;

        public string Message;
        public readonly List<FieldResult> Fields = new List<FieldResult>();

        public override string ToString()
        {
            return string.Format(CultureInfo.InvariantCulture,
                "step {0,-3} {1,-7} {2,6:F2}s {3,9}  {4}", Step, Outcome, Elapsed.TotalSeconds,
                Latency.HasValue
                    ? Latency.Value.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture) + "ms"
                    : "-",
                Message);
        }
    }

    public sealed class RunReport
    {
        public readonly List<StepResult> Steps = new List<StepResult>();
        public DateTime StartedUtc = DateTime.UtcNow;
        public TimeSpan Duration;

        /// <summary>Set when the run stopped early; the remaining rows were never attempted.</summary>
        public string AbortReason;
        public bool Aborted { get { return !string.IsNullOrEmpty(AbortReason); } }

        public int Passed { get { return Count(Outcome.Pass); } }
        public int Failed { get { return Count(Outcome.Fail); } }
        public int TimedOut { get { return Count(Outcome.Timeout); } }
        public bool AllPassed
        {
            get { return Failed == 0 && TimedOut == 0 && Steps.Count > 0 && !Aborted; }
        }

        private int Count(Outcome o)
        {
            int n = 0;
            foreach (var s in Steps) if (s.Outcome == o) n++;
            return n;
        }

        public string Summary()
        {
            var head = string.Format(CultureInfo.InvariantCulture,
                "{0} step(s): {1} passed, {2} failed, {3} timed out  in {4:F1}s",
                Steps.Count, Passed, Failed, TimedOut, Duration.TotalSeconds);

            if (Aborted) head += Environment.NewLine + "ABORTED: " + AbortReason;

            var lat = LatencySummary();
            return lat == null ? head : head + Environment.NewLine + lat;
        }

        /// <summary>
        /// Broker latency across every step that produced one. Reported with a p95 as well as a
        /// mean because a bridge that is usually fast and occasionally not is a different problem
        /// from one that is uniformly slow, and a mean hides the difference.
        /// </summary>
        public string LatencySummary()
        {
            var ms = new List<double>();
            foreach (var s in Steps) if (s.Latency.HasValue) ms.Add(s.Latency.Value.TotalMilliseconds);
            if (ms.Count == 0) return null;

            ms.Sort();
            double total = 0;
            foreach (var v in ms) total += v;

            return string.Format(CultureInfo.InvariantCulture,
                "broker latency over {0} step(s):  min {1:F1}ms   mean {2:F1}ms   p50 {3:F1}ms   " +
                "p95 {4:F1}ms   max {5:F1}ms",
                ms.Count, ms[0], total / ms.Count, Pct(ms, 0.50), Pct(ms, 0.95), ms[ms.Count - 1]);
        }

        private static double Pct(List<double> sorted, double p)
        {
            if (sorted.Count == 0) return 0;
            var i = (int)Math.Ceiling(p * sorted.Count) - 1;
            if (i < 0) i = 0;
            if (i >= sorted.Count) i = sorted.Count - 1;
            return sorted[i];
        }
    }

    /// <summary>Knobs for the DESIGN 9.1 verification loop.</summary>
    public sealed class RunOptions
    {
        /// <summary>Max wait for a step's expected state to appear on B.</summary>
        public TimeSpan Timeout = TimeSpan.FromSeconds(15);
        /// <summary>Shorter budget for steps expected to produce NOTHING (negative tests).</summary>
        public TimeSpan NegativeTimeout = TimeSpan.FromSeconds(4);
        /// <summary>Default numeric tolerance when a row does not specify one.</summary>
        public double DefaultTolerance = 0.5;
        /// <summary>Quiesce B before each stimulus so stragglers are not mis-attributed.</summary>
        public TimeSpan Quiesce = TimeSpan.FromMilliseconds(400);
        /// <summary>Warn when a pass lands within this fraction of the timeout (flakiness early-warning).</summary>
        public double NearTimeoutFraction = 0.75;

        /// <summary>
        /// Give up after this many CONSECUTIVE timeouts. 0 = never give up.
        ///
        /// A broker that has died does not fail a run - it makes every remaining row wait the full
        /// <see cref="Timeout"/> and then record "nothing arrived". At 4000 rows and 15s that is
        /// sixteen hours to be told something that was obvious after the third row. Nothing here
        /// distinguishes "the broker is gone" from "these particular rows do not cross", so this is
        /// off by default and opt-in for long runs.
        /// </summary>
        public int AbortAfterConsecutiveTimeouts;
    }
}
