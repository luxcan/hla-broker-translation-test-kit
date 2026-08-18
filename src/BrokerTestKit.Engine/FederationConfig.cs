using System.Collections.Generic;

namespace BrokerTestKit.Engine
{
    /// <summary>Which HLA API a federation is joined with.</summary>
    public enum HlaVersion
    {
        /// <summary>HLA 1.3 — Federation A side (RPR FOM 1.0, .fed file).</summary>
        Hla13,
        /// <summary>HLA Evolved / IEEE 1516-2010 — Federation B side (RPR FOM 2.0, FDD .xml).</summary>
        Hla1516e
    }

    /// <summary>
    /// Everything needed to join one federation. Two of these describe the A/B pair the
    /// broker translates between.
    /// </summary>
    public sealed class FederationConfig
    {
        /// <summary>Short label used in logs/reports, e.g. "A" or "B".</summary>
        public string Label = "A";

        public HlaVersion Version = HlaVersion.Hla13;

        /// <summary>Federation execution name.</summary>
        public string FederationName = "BTK-FedA";

        /// <summary>Federate type (both HLA versions). The 1.3 initializer has ONLY this.</summary>
        public string FederateType = "HarnessA";

        /// <summary>Federate name — HLA Evolved only (the 1.3 initializer has no such property).</summary>
        public string FederateName = null;

        /// <summary>.fed for HLA 1.3, FDD .xml for Evolved. Resolved via RTI_CONFIG.</summary>
        public string FedFile = "MAK-RPR1-1-1.fed";

        /// <summary>Additional FOM modules — Evolved only.</summary>
        public List<string> FomModules = null;

        /// <summary>MIM override — Evolved only. Null = RTI default.</summary>
        public string Mim = null;

        /// <summary>RPR FOM version: 1.0 for Fed A, 2.0 for Fed B.</summary>
        public double RprVersion = 1.0;

        /// <summary>Required for the Universal (by-name, raw byte[]) attribute path.</summary>
        public bool EnableGenericAttributes = true;

        public bool NetworkBigEndian = true;

        /// <summary>Federation A default: HLA 1.3 + RPR FOM 1.0.</summary>
        public static FederationConfig DefaultFedA()
        {
            return new FederationConfig
            {
                Label = "A",
                Version = HlaVersion.Hla13,
                FederationName = "BTK-FedA",
                FederateType = "HarnessA",
                FedFile = "MAK-RPR1-1-1.fed",
                RprVersion = 1.0,
            };
        }

        /// <summary>Federation B default: HLA Evolved + RPR FOM 2.0.</summary>
        public static FederationConfig DefaultFedB()
        {
            return new FederationConfig
            {
                Label = "B",
                Version = HlaVersion.Hla1516e,
                FederationName = "BTK-FedB",
                FederateType = "HarnessB",
                FederateName = "HarnessB",
                FedFile = "RPR_FOM_v2.0_1516-2010.xml",
                RprVersion = 2.0,
            };
        }

        public override string ToString()
        {
            return string.Format("Fed{0} [{1}] exec='{2}' fed='{3}' rpr={4}",
                Label, Version, FederationName, FedFile, RprVersion);
        }
    }
}
