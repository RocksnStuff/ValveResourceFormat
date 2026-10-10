using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ValveKeyValue;
using ValveResourceFormat;
using ValveResourceFormat.IO;
using ValveResourceFormat.Serialization.KeyValues;

namespace Tests.IO
{
    public class PulseGraphExtractTest
    {
        private sealed class CollectingProgress : IProgress<string>
        {
            public List<string> Messages { get; } = [];

            public void Report(string value) => Messages.Add(value);
        }

        private static (KVDocument Document, List<string> Messages) Decompile(string name)
        {
            using var resource = new Resource
            {
                FileName = name,
            };
            resource.Read(TestFixtures.Path(name));

            var progress = new CollectingProgress();
            var text = new PulseGraphExtract(resource) { ProgressReporter = progress }.ToDocumentString();

            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
            return (KVSerializer.Create(KVSerializationFormat.KeyValues3Text).Deserialize(stream), progress.Messages);
        }

        private static List<KVObject> Nodes(KVDocument document) => [.. document.Root.GetArray("m_Children")];

        private static KVObject Node(KVDocument document, int id) => Nodes(document).Single(node => node.GetInt32Property("m_NodeID") == id);

        private static List<KVObject> Children(KVObject node, string className)
            => [.. (node.GetArray("m_Children") ?? []).Where(child => child.GetStringProperty("_class") == className)];

        [Test]
        public async Task DecompilesPulseGraphToDocument()
        {
            var (document, messages) = Decompile("de_inferno_script.vpulse_c");
            var nodes = Nodes(document);

            var method = nodes.Single(node => node.GetStringProperty("_class") == "Inflow_Method");
            var branch = nodes.Single(node => node.GetStringProperty("_class") == "Outflow_If");
            var condition = Children(branch, "Connection_Inparam").Single();
            var conditionSource = Node(document, condition.GetInt32Property("m_SourceNode"));

            using (Assert.Multiple())
            {
                await Assert.That(messages).IsEmpty();
                await Assert.That(document.Header!.Format.Name).IsEqualTo("vpulse17");
                await Assert.That(document.Root.GetStringProperty("_class")).IsEqualTo("Root");
                await Assert.That(document.Root.GetStringProperty("m_DomainIdentifier")).IsEqualTo("ServerEntity");
                await Assert.That(document.Root.GetStringProperty("m_ParentMapName")).IsEqualTo("maps/de_inferno.vmap");

                await Assert.That(method.GetStringProperty("m_Name")).IsEqualTo("GameModeCheck");
                await Assert.That(Children(method, "Connection_Outflow").Single().GetInt32Property("m_DestFlowNodeID")).IsEqualTo(branch.GetInt32Property("m_NodeID"));

                await Assert.That(condition.GetStringProperty("m_ThisInparamName")).IsEqualTo("m_bCondition");
                await Assert.That(conditionSource.GetStringProperty("m_GenericNodeType")).IsEqualTo("CCSPointPulseAPI::GetIsWingman");
                await Assert.That(Children(branch, "Connection_Outflow").Select(connection => connection.GetStringProperty("m_SourceOutflowName")))
                    .IsEquivalentTo(["m_IsTrue", "m_IsFalse"]);

                await Assert.That(nodes.Count(node => node.GetStringProperty("_class") == "Step_EntFire")).IsEqualTo(10);
            }
        }

        // The fixture was compiled from a document using each node type the decompiler rebuilds natively, decompiling it
        // must give that document back
        [Test]
        public async Task DecompilesNativeNodeTypes()
        {
            var (document, messages) = Decompile("pulse_native_nodes.vpulse_c");
            var classes = Nodes(document).GroupBy(node => node.GetStringProperty("_class")).ToDictionary(group => group.Key, group => group.Count());

            using (Assert.Multiple())
            {
                await Assert.That(messages).IsEmpty();
                await Assert.That(classes).IsEquivalentTo(new Dictionary<string, int>
                {
                    ["VariableDefinition"] = 2,
                    ["PublicOutputDefinition"] = 1,
                    ["Inflow_Method"] = 3,
                    ["Inflow_EntOutput"] = 1,
                    ["ExprAdd"] = 1,
                    ["ExprNot"] = 1,
                    ["ExprTernaryConditional"] = 1,
                    ["Step_SetVar"] = 2,
                    ["Step_PublicOutput"] = 1,
                    ["Outflow_Return"] = 1,
                    ["Step_CallMethod"] = 3,
                    ["Outflow_If"] = 1,
                    ["Step_EntFire"] = 2,
                    ["Outflow_IntSwitch"] = 1,
                    ["Step_DebugLog"] = 4,
                    ["Outflow_LoopIntegerRange"] = 1,
                    ["Step_CreateTempVar"] = 1,
                    ["Outflow_LoopWhile"] = 1,
                    ["Outflow_Break"] = 1,
                    ["Step_Timeline"] = 1,
                    ["Outflow_Cycle"] = 1,
                    ["Outflow_Simultaneous"] = 1,
                    ["Step_FireCursors"] = 1,
                });

                // Method argument read through a connection, and the call passing it a literal
                await Assert.That(Children(Node(document, 15), "Connection_Inparam").Single().GetStringProperty("m_SourceOutparamName")).IsEqualTo("amount");
                await Assert.That(Node(document, 50).GetSubCollection("m_ArgList").GetSubCollection("amount").GetInt32Property("m_LiteralValue")).IsEqualTo(7);

                // Loop body reads the loop index
                var indexRead = Children(Node(document, 110), "Connection_Inparam").Single();
                await Assert.That(indexRead.GetInt32Property("m_SourceNode")).IsEqualTo(100);
                await Assert.That(indexRead.GetStringProperty("m_SourceOutparamName")).IsEqualTo("Index");

                await Assert.That(Node(document, 80).GetArray<int>("m_Cases")).IsEquivalentTo([1, 2]);
                await Assert.That(Node(document, 140).GetSubCollection("m_Message").GetStringProperty("m_ValueSource")).IsEqualTo("local_name_ref");
                await Assert.That(Node(document, 170).GetStringProperty("m_nCycleMode")).IsEqualTo("PulseCycleMode_t::SHUFFLED");
                await Assert.That(Node(document, 190).GetStringProperty("m_nCallMode")).IsEqualTo("ASYNC_FIRE_AND_FORGET");
                await Assert.That(Node(document, 220).GetStringProperty("m_nStandardYieldingFlow_CallMode")).IsEqualTo("ASYNC_FIRE_AND_FORGET");
                await Assert.That(Node(document, 310).GetStringProperty("m_nFireMode")).IsEqualTo("PulseEntFireMode_t::HANDLE");
            }
        }

        // Nodes backed by the game's pulse bindings are rebuilt as generic nodes named like the compiled bindings
        [Test]
        public async Task DecompilesGenericAndVariableReferenceNodes()
        {
            var (document, messages) = Decompile("de_train_script.vpulse_c");
            var nodes = Nodes(document);

            var genericTypes = nodes
                .Where(node => node.GetStringProperty("_class") is "Flow_Generic" or "Value_Generic")
                .Select(node => node.GetStringProperty("m_GenericNodeType"))
                .ToList();

            var tagCursor = nodes.Single(node => node.GetStringProperty("m_GenericNodeType") == "CPulseCursorFuncs::TagCursor");
            var tagValue = tagCursor.GetSubCollection("m_GenericPorts").GetSubCollection("tagValue");

            var randomInt = nodes.Single(node => node.GetStringProperty("m_GenericNodeType") == "CPulseCell_Value_RandomInt");
            var randomIntInputs = Children(randomInt, "Connection_Inparam").Select(connection => Node(document, connection.GetInt32Property("m_SourceNode"))).ToList();

            using (Assert.Multiple())
            {
                await Assert.That(messages).IsEmpty();
                await Assert.That(genericTypes).IsEquivalentTo([
                    "CCSPointPulseAPI::OnRoundStart",
                    "CCSPointPulseAPI::OnRoundEnd",
                    "CPulseCell_Inflow_Wait",
                    "CPulseCell_Value_RandomInt",
                    "CPulseCursorFuncs::KillCursorsWithTag",
                    "CPulseCursorFuncs::KillCursorsWithTag",
                    "CPulseCursorFuncs::KillCursorsWithTag",
                    "CPulseCursorFuncs::TagCursor",
                ]);

                // A literal passed to a port of any type states its own type
                await Assert.That(tagValue.GetStringProperty("m_UserRefinedType")).IsEqualTo("PVAL_STRING");

                // Variables read through Var Reference nodes keep those nodes
                await Assert.That(randomIntInputs.Select(node => node.GetStringProperty("_class"))).IsEquivalentTo(["Value_VarReference", "Value_VarReference"]);
                await Assert.That(nodes.Single(node => node.GetInt32Property("m_NodeID") == 246579145).GetInt32Property("m_SourceID")).IsEqualTo(937282370);
            }
        }

        // Skipped nodes compile to nothing, a skipped entity fire is brought back from the output connection it lists
        [Test]
        public async Task DecompilesSkippedEntityFire()
        {
            var (document, messages) = Decompile("dl_hideout.vpulse_c");
            var skipped = Node(document, 420814798);
            var previous = Children(Node(document, 811898506), "Connection_Outflow").Single();

            using (Assert.Multiple())
            {
                await Assert.That(messages).Contains("Node 420814798 is a skipped entity fire, its parameter value is not kept.");
                await Assert.That(skipped.GetStringProperty("_class")).IsEqualTo("Step_EntFire");
                await Assert.That(skipped.GetBooleanProperty("m_bNodeSkip")).IsTrue();
                await Assert.That(skipped.GetStringProperty("m_Input")).IsEqualTo("SetControlPoint");
                await Assert.That(skipped.GetSubCollection("m_TargetName").GetStringProperty("m_LiteralValue")).IsEqualTo("fortune_teller_fx_on");
                await Assert.That(previous.GetInt32Property("m_DestFlowNodeID")).IsEqualTo(420814798);
                await Assert.That(previous.GetStringProperty("m_DestInflowName")).IsEqualTo("InDefault");
            }
        }

        [Test]
        public async Task DecompilesEntityOutputListener()
        {
            var (document, messages) = Decompile("bobb_test.vpulse_c");
            var keys = Node(document, 9371199).GetSubCollection("m_GenericKeys");

            using (Assert.Multiple())
            {
                await Assert.That(messages).IsEmpty();
                await Assert.That(keys.GetSubCollection("m_strEntityOutput").GetStringProperty("m_strEntityOutput")).IsEqualTo("OnDamaged");
                await Assert.That(keys.GetBooleanProperty("m_bListenUntilCanceled")).IsTrue();
            }
        }

        // The compiler numbers method argument registers by the ids of the connections reading them
        [Test]
        public async Task OrdersArgumentConnectionsByRegister()
        {
            var (document, _) = Decompile("trooper_path_test.vpulse_c");
            var reads = Nodes(document)
                .SelectMany(node => Children(node, "Connection_Inparam"))
                .Where(connection => connection.GetInt32Property("m_SourceNode") == 372291322)
                .OrderBy(connection => connection.GetInt32Property("m_NodeID"))
                .Select(connection => connection.GetStringProperty("m_SourceOutparamName"))
                .ToList();

            await Assert.That(string.Join(',', reads)).IsEqualTo("button,spawner,spawner");
        }

        [Test]
        public async Task ExtractsPulseGraphAsVpulse()
        {
            var file = TestFixtures.Path("pulse_native_nodes.vpulse_c");
            using var resource = new Resource
            {
                FileName = file,
            };
            resource.Read(file);

            using var contentFile = FileExtract.Extract(resource, new NullFileLoader());

            using (Assert.Multiple())
            {
                await Assert.That(FileExtract.GetExtension(resource)).IsEqualTo("vpulse");
                await Assert.That(Encoding.UTF8.GetString(contentFile.Data!)).Contains("format:vpulse17:");
            }
        }
    }
}
