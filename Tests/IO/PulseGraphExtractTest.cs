using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Tests.Resources;
using ValveKeyValue;
using ValveResourceFormat;
using ValveResourceFormat.IO;
using ValveResourceFormat.ResourceTypes;
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

        private static (string Text, List<string> Messages) Decompile(string name, bool stripDebugInfo = false)
        {
            using var resource = new Resource
            {
                FileName = name,
            };
            resource.Read(TestFixtures.Path(name));

            if (stripDebugInfo)
            {
                StripDebugInfo(((BinaryKV3)resource.DataBlock!).Data);
            }

            var progress = new CollectingProgress();
            return (new PulseGraphExtract(resource) { ProgressReporter = progress }.ToDocumentString(), progress.Messages);
        }

        // Together the samples hold every node type the decompiler rebuilds
        [Test]
        [Arguments("pulse_native_nodes.vpulse_c")]
        [Arguments("bobb_test.vpulse_c")]
        [Arguments("pulse_loops_arrays_blackboard.vpulse_c")]
        public async Task DecompilesPulseGraph(string name)
        {
            var (text, messages) = Decompile(name);

            await Assert.That(messages).IsEmpty();
            await ValidOutput.VerifyText(text, name, "decompiled.vpulse");
        }

        // Without debug info the editor nodes are guessed, so recompiling may give different bytecode
        [Test]
        [Arguments("pulse_native_nodes.vpulse_c")]
        [Arguments("bobb_test.vpulse_c")]
        [Arguments("pulse_loops_arrays_blackboard.vpulse_c")]
        public async Task DecompilesPulseGraphWithoutDebugInfo(string name)
        {
            var (text, _) = Decompile(name, stripDebugInfo: true);

            await ValidOutput.VerifyText(text, name, "decompiled_nodebug.vpulse");
        }

        private static void StripDebugInfo(KVObject graph)
        {
            foreach (var chunk in graph.GetArray("m_Chunks"))
            {
                chunk.Remove("m_InstructionDebugInfos");

                foreach (var register in chunk.GetArray("m_Registers") ?? [])
                {
                    register.Remove("m_OriginName");
                    register.Remove("m_nWrittenByInstruction");
                }
            }
        }

        // The compiler numbers method argument registers by the ids of the connections reading them
        [Test]
        public async Task OrdersArgumentConnectionsByRegister()
        {
            var (text, _) = Decompile("trooper_path_test.vpulse_c");

            var reads = TestFixtures.ParseKV3(text).GetArray("m_Children")
                .SelectMany(node => node.GetArray("m_Children") ?? [])
                .Where(connection => connection.GetStringProperty("_class") == "Connection_Inparam" && connection.GetInt32Property("m_SourceNode") == 372291322)
                .OrderBy(connection => connection.GetInt32Property("m_NodeID"))
                .Select(connection => connection.GetStringProperty("m_SourceOutparamName"));

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
