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
        public TimeSpan Elapsed;
        public string Message;
        public readonly List<FieldResult> Fields = new List<FieldResult>();

        public override string ToString()
        {
            return string.Format(CultureInfo.InvariantCulture,
                "step {0,-3} {1,-7} {2,6:F2}s  {3}", Step, Outcome, Elapsed.TotalSeconds, Message);
        }
    }

    public sealed class RunReport
    {
        public readonly List<StepResult> Steps = new List<StepResult>();
        public DateTime StartedUtc = DateTime.UtcNow;
        public TimeSpan Duration;

        public int Passed { get { return Count(Outcome.Pass); } }
        public int Failed { get { return Count(Outcome.Fail); } }
        public int TimedOut { get { return Count(Outcome.Timeout); } }
        public bool AllPassed { get { return Failed == 0 && TimedOut == 0 && Steps.Count > 0; } }

        private int Count(Outcome o)
        {
            int n = 0;
            foreach (var s in Steps) if (s.Outcome == o) n++;
            return n;
        }

        public string Summary()
        {
            return string.Format(CultureInfo.InvariantCulture,
                "{0} step(s): {1} passed, {2} failed, {3} timed out  in {4:F1}s",
                Steps.Count, Passed, Failed, TimedOut, Duration.TotalSeconds);
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
    }
}
