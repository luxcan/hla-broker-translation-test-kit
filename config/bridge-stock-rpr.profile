# =============================================================================
#  The rig under test: stock RPR 1.0 (HLA 1.3) -> VR-Exchange 2.9 -> stock RPR 2.0 (Evolved)
#
#  This is the configuration proven by tests/native/run-bridge.ps1. Everything here was
#  measured, not assumed; the notes say which mistake each line prevents.
#
#      BrokerTestKit.Cli.exe entity in.csv out.csv --profile config\bridge-stock-rpr.profile
#
#  The broker side of the same rig is C:\MAK\vrexchange2.9\bin64\BTK-Bridge.xml plus the two
#  .bcf files; start it with  vrx.exe -l BTK-Bridge.xml -G  before running any test.
#
#  Paths may be absolute or relative to THIS file.
# =============================================================================

licence = 27000@127.0.0.1

# -----------------------------------------------------------------------------
[A]
# Federation A - RPR 1.0, HLA 1.3, MAK RTI 4.5.1 (vc14 build).
#
# vc14 and not the VC10 build of the same 4.5.1: VR-Exchange 2.9 is VC14 and rejects a VC10
# library outright ("Expected VC14 but found VC10"). That check is about ONE PROCESS - a VC10
# federate still joins this federation, because the wire protocol belongs to the RTI VERSION.
# -----------------------------------------------------------------------------
agent         = btk-agent13.exe
rtiBin        = C:\MAK\makRti4.5.1(vc14)\bin
ridFile       = rid-fedA-rti451.mtl
assistantPort = 6003
configDir     = .

exec          = BTK-FedA
fedFile       = MAK-RPR1-1-1.fed
federateName  = BtkRunnerA
federateType  = Harness

# class= / alias= are OPTIONAL overrides. Left out, the class comes from the CSV's `# class:`
# directive and is resolved through the FOM into the name the RTI knows - which differs per HLA
# version: ObjectRoot.BaseEntity....GroundVehicle here, HLAobjectRoot.… on the Evolved side.
# Set them only when the FOM cannot be trusted to name the class, e.g. a hand-edited FDD.
# class       = ObjectRoot.BaseEntity.PhysicalEntity.Platform.GroundVehicle
# alias       = GroundVehicle
fom           = MAK-RPR1-1-1.omt

# -----------------------------------------------------------------------------
[B]
# Federation B - RPR 2.0, HLA Evolved (1516-2010), MAK RTI 5.0.1.
# -----------------------------------------------------------------------------
agent         = btk-agent1516e.exe
rtiBin        = C:\MAK\makRti5.0.1\bin
ridFile       = rid-fedB-rti501.mtl

# A DIFFERENT Assistant port from A. Two RTI versions sharing 6003 ends with the second one
# dropped: "Invalid message version from Assistant. Disconnecting." It is an environment
# variable, not a rid setting, and is documented only in the VR-Exchange Users Guide 5.3.
assistantPort = 6004

# 🚨 The VR-Exchange bin64, NOT config\. The joinFom modules below have to resolve to the
# BROKER'S OWN copies - see the joinFom note. RPR_FOM_v2.0_1516-2010.xml is byte-identical in
# both folders, so pointing here costs nothing on that file and fixes the modules.
configDir     = C:\MAK\vrexchange2.9\bin64

exec          = BTK-FedB
fedFile       = RPR_FOM_v2.0_1516-2010.xml

# 🚨 THE line that makes the bridge work.
#
# The VR-Exchange 1516e broker does not join with the bare RPR 2.0 FDD: VR-Link reads
# bin64\MAK-1516e-Config.xml and brings these 16 further modules to the join. A federate that
# joins with only the base FDD ends up with a different FDD from the broker and receives
# NOTHING AT ALL - no exception, no warning, no log line. Keep this list in step with
# MAK-1516e-Config.xml if VR-Exchange is upgraded.
joinFom       = NETN-BASE.xml,NETN-Physical.xml,NETN-METOC.xml,NETN-MRM.xml,MAK-Physical-2_evolved.xml,MAK-Aerodrome-1_evolved.xml,MAK-METOC-1_evolved.xml,MAK-VRFExt-9_evolved.xml,MAK-DIGuy-7_evolved.xml,MAK-LgrControl-2_evolved.xml,MAK-VRFAggregate-6_evolved.xml,MAK-DynamicTerrain-2_evolved.xml,MAK-VRLExt-3_evolved.xml,MAK-DER-1_evolved.xml,RPR-Enumerations_Experimental_IFF.xml,RPR-MAK_Experimental_IFF-3.xml

federateName  = BtkRunnerB
federateType  = Harness

# Subscribe at GroundVehicle - which is what the CSV's `# class: GroundVehicle` gives you. The
# broker registers the MAK-extended MAK_GroundVehicle (GroundVehicle -> NETN_GroundVehicle ->
# MAK_GroundVehicle); class promotion reports it here as GroundVehicle. Subscribe to an ANCESTOR
# instead and the object is discovered AS that ancestor carrying only that ancestor's attributes
# - which reads like a half-broken bridge but is only a misplaced subscription.
fom           = RPR_FOM_v2.0_1516-2010.xml
