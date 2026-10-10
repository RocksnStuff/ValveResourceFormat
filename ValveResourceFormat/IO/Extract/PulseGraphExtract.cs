using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using ValveKeyValue;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;

namespace ValveResourceFormat.IO;

/// <summary>
/// Decompiles a compiled Pulse graph back into an editable <c>.vpulse</c> document that resourcecompiler can build again.
/// </summary>
/// <remarks>
/// The compiled graph is register bytecode, but every instruction keeps the editor node it came from and the sequence point
/// it ran at. The document is rebuilt by walking each chunk the way the cursor would, creating one node per editor node and
/// connecting them in the order the flow reaches them.
/// </remarks>
public sealed partial class PulseGraphExtract
{
    /// <summary>Receives diagnostics about parts of the graph that could not be reconstructed exactly.</summary>
    public IProgress<string>? ProgressReporter { get; init; }

    private readonly KVObject graph;

    private readonly IReadOnlyList<KVObject> cells;
    private readonly IReadOnlyList<KVObject> chunks;
    private readonly IReadOnlyList<KVObject> invokeBindings;
    private readonly IReadOnlyList<KVObject> callInfos;
    private readonly IReadOnlyList<KVObject> constants;
    private readonly IReadOnlyList<KVObject> domainValues;
    private readonly IReadOnlyList<KVObject> variables;
    private readonly IReadOnlyList<KVObject> publicOutputs;

    // Editor node ids already present in the compiled graph, fresh ids are allocated past them
    private readonly HashSet<int> usedNodeIds = [];
    private int nextNodeId = 1;

    private readonly int[] cellNodeIds;
    private readonly int[] variableNodeIds;
    private readonly int[] publicOutputNodeIds;

    private readonly Dictionary<int, DocNode> nodes = [];
    private readonly List<DocNode> nodeOrder = [];
    private readonly HashSet<string> reportedMessages = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="PulseGraphExtract"/> class.
    /// </summary>
    /// <param name="resource">The compiled pulse graph resource.</param>
    public PulseGraphExtract(Resource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);

        graph = (resource.DataBlock as BinaryKV3)?.Data
            ?? throw new InvalidDataException("Resource DataBlock is not a BinaryKV3.");

        cells = graph.GetArray("m_Cells") ?? [];
        chunks = graph.GetArray("m_Chunks") ?? [];
        invokeBindings = graph.GetArray("m_InvokeBindings") ?? [];
        callInfos = graph.GetArray("m_CallInfos") ?? [];
        constants = graph.GetArray("m_Constants") ?? [];
        domainValues = graph.GetArray("m_DomainValues") ?? [];
        variables = graph.GetArray("m_Vars") ?? [];
        publicOutputs = graph.GetArray("m_PublicOutputs") ?? [];

        CollectUsedNodeIds();

        cellNodeIds = [.. cells.Select(cell => KnownOrFreshNodeId(cell.GetInt32Property("m_nEditorNodeID", -1)))];
        variableNodeIds = [.. variables.Select(variable => KnownOrFreshNodeId(variable.GetInt32Property("m_nEditorNodeID", -1)))];
        publicOutputNodeIds = [.. publicOutputs.Select(_ => AllocateNodeId())];
    }

    /// <summary>
    /// Converts the graph to a content file holding the <c>.vpulse</c> document.
    /// </summary>
    public ContentFile ToContentFile()
    {
        return new ContentFile
        {
            Data = Encoding.UTF8.GetBytes(ToDocumentString()),
        };
    }

    /// <summary>
    /// Builds the <c>.vpulse</c> document as KV3 text.
    /// </summary>
    public string ToDocumentString() => BuildDocument().ToKV3String(KV3IDLookup.Get("vpulse17"));

    private KVObject BuildDocument()
    {
        foreach (var (cellIndex, cell) in cells.Index())
        {
            if (cell.ContainsKey("m_EntryChunk"))
            {
                AddEntryNode(cellIndex, cell);
            }
        }

        WalkAllPaths();

        foreach (var nodeId in fireAndForgetNodes)
        {
            if (nodes.TryGetValue(nodeId, out var node))
            {
                node.Fields.Add("m_nStandardYieldingFlow_CallMode", "ASYNC_FIRE_AND_FORGET");
            }
        }

        OrderOutparamConnectionIds();

        var root = KVObject.Collection();
        root.Add("_class", "Root");
        root.Add("m_NodeID", AllocateNodeId());

        var children = KVObject.Array();

        foreach (var (index, variable) in variables.Index())
        {
            children.Add(BuildVariableDefinition(variable, variableNodeIds[index]));
        }

        foreach (var (index, output) in publicOutputs.Index())
        {
            children.Add(BuildPublicOutputDefinition(output, publicOutputNodeIds[index]));
        }

        LayoutNodes();

        foreach (var node in nodeOrder)
        {
            children.Add(node.ToKeyValues());
        }

        root.Add("m_Children", children);
        root.Add("m_DomainIdentifier", graph.GetStringProperty("m_DomainIdentifier", string.Empty));
        root.Add("m_DomainSubType", graph.GetStringProperty("m_DomainSubType", "PVAL_VOID"));
        root.Add("m_ParentMapName", graph.GetStringProperty("m_ParentMapName", string.Empty));
        root.Add("m_ParentXmlName", graph.GetStringProperty("m_ParentXmlName", string.Empty));

        return root;
    }

    private void Report(string message)
    {
        if (reportedMessages.Add(message))
        {
            ProgressReporter?.Report(message);
        }
    }

    #region Node ids
    private void CollectUsedNodeIds()
    {
        void Use(int id)
        {
            if (id >= 0)
            {
                usedNodeIds.Add(id);
            }
        }

        foreach (var cell in cells)
        {
            Use(cell.GetInt32Property("m_nEditorNodeID", -1));
        }

        foreach (var variable in variables)
        {
            Use(variable.GetInt32Property("m_nEditorNodeID", -1));
        }

        foreach (var callInfo in callInfos)
        {
            Use(callInfo.GetInt32Property("m_nEditorNodeID", -1));
            Use(callInfo.GetInt32Property("m_CallMethodID", -1));
        }

        foreach (var chunk in chunks)
        {
            foreach (var debugInfo in chunk.GetArray("m_InstructionDebugInfos") ?? [])
            {
                Use(debugInfo.GetInt32Property("m_nFlowNodeID", -1));
                Use(debugInfo.GetInt32Property("m_nValueNodeID", -1));
            }

            foreach (var register in chunk.GetArray("m_Registers") ?? [])
            {
                if (TryParseOriginName(register.GetStringProperty("m_OriginName", string.Empty), out var nodeId, out _))
                {
                    Use(nodeId);
                }
            }
        }

        nextNodeId = usedNodeIds.Count > 0 ? usedNodeIds.Max() + 1 : 1;
    }

    private int AllocateNodeId()
    {
        while (!usedNodeIds.Add(nextNodeId))
        {
            nextNodeId++;
        }

        return nextNodeId++;
    }

    private int KnownOrFreshNodeId(int id) => id >= 0 ? id : AllocateNodeId();

    /// <summary>
    /// The compiler numbers the outparam registers of an entry node by the ids of the connections reading them,
    /// so those connections take their ids in register order.
    /// </summary>
    private void OrderOutparamConnectionIds()
    {
        foreach (var (sourceNode, registers) in entryOutparamRegisters)
        {
            var connections = nodeOrder
                .SelectMany(node => node.InparamConnectionsFrom(sourceNode))
                .ToList();

            var ids = connections.Select(connection => connection.Id).Order().ToList();
            var ordered = connections
                .OrderBy(connection => registers.GetValueOrDefault(connection.SourcePort, int.MaxValue))
                .ThenBy(connection => connection.Id)
                .ToList();

            var newIds = ordered.Index().ToDictionary(item => item.Item.Id, item => ids[item.Index]);

            foreach (var node in nodeOrder)
            {
                node.RenumberInparamConnections(newIds);
            }
        }
    }

    /// <summary>
    /// Register origins read "nodeid:port". Compiler generated registers such as casts have no node of their own.
    /// </summary>
    private static bool TryParseOriginName(string originName, out int nodeId, out string port)
    {
        var separator = originName.IndexOf(':', StringComparison.Ordinal);
        if (separator > 0 && int.TryParse(originName.AsSpan(0, separator), NumberStyles.Integer, CultureInfo.InvariantCulture, out nodeId))
        {
            port = originName[(separator + 1)..];
            return true;
        }

        nodeId = -1;
        port = string.Empty;
        return false;
    }
    #endregion

    #region Definitions
    private static KVObject BuildVariableDefinition(KVObject variable, int nodeId)
    {
        var node = KVObject.Collection();
        node.Add("_class", "VariableDefinition");
        node.Add("m_NodeID", nodeId);
        node.Add("m_Name", variable.GetStringProperty("m_Name", string.Empty));

        var value = KVObject.Collection();
        value.Add("m_Type", variable.GetStringProperty("m_Type", "PVAL_INVALID"));
        value.Add("m_Value", variable.TryGetValue("m_DefaultValue", out var defaultValue) ? defaultValue : KVObject.Null());
        node.Add("m_Value", value);

        node.Add("m_Description", variable.GetStringProperty("m_Description", string.Empty));
        node.Add("m_nKeysSource", variable.GetStringProperty("m_nKeysSource", "PRIVATE"));
        node.Add("m_bIsPublicBlackboardVariable", variable.GetBooleanProperty("m_bIsPublicBlackboardVariable"));
        return node;
    }

    private static KVObject BuildPublicOutputDefinition(KVObject output, int nodeId)
    {
        var node = KVObject.Collection();
        node.Add("_class", "PublicOutputDefinition");
        node.Add("m_NodeID", nodeId);
        node.Add("m_Name", output.GetStringProperty("m_Name", string.Empty));
        node.Add("m_Description", output.GetStringProperty("m_Description", string.Empty));
        node.Add("m_Args", BuildMethodArgs(output.GetArray("m_Args")));
        return node;
    }

    private static KVObject BuildMethodArgs(IReadOnlyList<KVObject>? args)
    {
        var array = KVObject.Array();

        foreach (var arg in args ?? [])
        {
            var entry = KVObject.Collection();
            entry.Add("m_Name", arg.GetStringProperty("m_Name", string.Empty));
            entry.Add("m_Type", arg.GetStringProperty("m_Type", "PVAL_INVALID"));
            entry.Add("m_Description", arg.GetStringProperty("m_Description", string.Empty));
            array.Add(entry);
        }

        return array;
    }
    #endregion

    #region Values and ports
    /// <summary>What a register holds at some point of the flow, as the document would express it.</summary>
    private abstract record ValueRef;

    private sealed record LiteralValue(KVObject Value, string Type) : ValueRef;

    private sealed record DomainValue(KVObject Value) : ValueRef;

    private sealed record VariableValue(int VariableNodeId) : ValueRef;

    private sealed record NodeOutput(int NodeId, string Port) : ValueRef;

    // A temporary variable, read by its name
    private sealed record LocalNameValue(string Name) : ValueRef;

    // A branch condition the node inverts itself rather than reading from a separate Not node
    private sealed record InvertedValue(ValueRef Inner) : ValueRef;

    // Bookkeeping a flow node computes for itself, such as a loop's bounds checks
    private sealed record InternalValue(string Port, ValueRef? First, ValueRef? Second) : ValueRef;

    // One case test of a switch: the switched on value against a case value
    private sealed record CaseCondition(ValueRef SwitchValue, ValueRef CaseValue, string SwitchType) : ValueRef;

    private sealed record UnknownValue : ValueRef;

    /// <summary>
    /// Writes an input port onto <paramref name="container"/>. Values produced by other nodes are wired with an inparam
    /// connection on the consuming node, everything else is stored inline on the port.
    /// </summary>
    /// <param name="node">The node owning the port.</param>
    /// <param name="container">Where the port is stored, the node's fields or a table of ports.</param>
    /// <param name="key">The key of the port in <paramref name="container"/>.</param>
    /// <param name="portName">The port's name, as connections refer to it.</param>
    /// <param name="value">The value the port reads.</param>
    /// <param name="refineLiteralType">Whether a literal must state its type, for ports that take any type.</param>
    private void SetInputPort(DocNode node, KVObject container, string key, string portName, ValueRef value, bool refineLiteralType = false)
    {
        var port = KVObject.Collection();

        switch (value)
        {
            case LiteralValue literal:
                port.Add("m_ValueSource", "literal");
                port.Add("m_LiteralValue", literal.Value);

                if (refineLiteralType)
                {
                    port.Add("m_UserRefinedType", literal.Type);
                }

                break;

            case DomainValue domainValue:
                port.Add("m_ValueSource", "domain_value");
                port.Add("m_LiteralValue", domainValue.Value);
                break;

            case VariableValue variable:
                port.Add("m_ValueSource", "var");
                port.Add("m_nVarOrConstID", variable.VariableNodeId);
                break;

            case LocalNameValue local:
                port.Add("m_ValueSource", "local_name_ref");
                port.Add("m_LiteralValue", local.Name);
                break;

            case NodeOutput output:
                port.Add("m_ValueSource", "port");
                node.AddInparamConnection(AllocateNodeId(), output.NodeId, output.Port, portName);
                break;

            case InvertedValue inverted:
                Report($"Node {node.Id} reads an inverted value through '{portName}' that could not be expressed, the inversion is lost.");
                SetInputPort(node, container, key, portName, inverted.Inner);
                return;

            default:
                Report($"Node {node.Id} port '{portName}' reads a value that could not be resolved.");
                port.Add("m_ValueSource", "literal");
                break;
        }

        container[key] = port;
    }
    #endregion

    #region Layout
    private const float ColumnWidth = 320f;
    private const float RowHeight = 160f;

    // Flow nodes go in columns by their distance from an entry point, the first node an outflow leads to on the same
    // row and the others below it. Each entry starts a new band of rows. Value nodes sit just left of their first reader.
    private void LayoutNodes()
    {
        var placed = new HashSet<int>();
        var nextFreeRow = new Dictionary<int, float>();

        void Place(DocNode node, int column, float y)
        {
            y = Math.Max(y, nextFreeRow.GetValueOrDefault(column));
            node.Position = new Vector2(column * ColumnWidth, y);
            nextFreeRow[column] = y + RowHeight;

            foreach (var target in node.OutflowTargets)
            {
                if (nodes.TryGetValue(target, out var next) && next.IsFlow && placed.Add(next.Id))
                {
                    Place(next, column + 1, y);
                }
            }
        }

        float BandBottom() => nextFreeRow.Count > 0 ? nextFreeRow.Values.Max() + RowHeight : 0f;

        foreach (var entry in nodeOrder.Where(node => node.IsEntry).Concat(nodeOrder.Where(node => node.IsFlow)))
        {
            if (placed.Add(entry.Id))
            {
                Place(entry, 0, BandBottom());
            }
        }

        var bandTop = BandBottom();

        // A value is placed once its reader is, so chains of values extend further left. Values nothing reads go below.
        var pending = nodeOrder.Where(node => !node.IsFlow).ToList();
        var stackedPerReader = new Dictionary<int, int>();

        while (pending.Count > 0)
        {
            var placedAny = false;

            foreach (var node in pending.ToList())
            {
                var reader = nodeOrder.FirstOrDefault(other => placed.Contains(other.Id) && other.ReadsFrom(node.Id));
                if (reader == null)
                {
                    continue;
                }

                var stack = stackedPerReader.GetValueOrDefault(reader.Id);
                stackedPerReader[reader.Id] = stack + 1;

                node.Position = new Vector2(reader.Position.X - (ColumnWidth * 0.75f), reader.Position.Y + ((stack + 1) * RowHeight * 0.5f));
                placed.Add(node.Id);
                pending.Remove(node);
                placedAny = true;
            }

            if (!placedAny)
            {
                pending[0].Position = new Vector2(0, bandTop);
                placed.Add(pending[0].Id);
                pending.RemoveAt(0);
                bandTop += RowHeight;
            }
        }
    }
    #endregion

    #region Document nodes
    private sealed class DocNode(int id, string className)
    {
        public int Id { get; } = id;
        public string ClassName { get; } = className;
        public string? Name { get; set; }
        public KVObject Fields { get; } = KVObject.Collection();
        public bool IsFlow { get; set; } = true;
        public bool IsEntry { get; set; }
        public Vector2 Position { get; set; }

        // The inflow other flows always connect into, null takes it from the sequence point the cursor arrived at
        public string? InflowName { get; set; }

        // The inflow used when the cursor arrived at a sequence point that names no inflow
        public string FallbackInflowName { get; set; } = "InDefault";

        private readonly List<(string Outflow, int Target, string Inflow)> outflows = [];
        private readonly List<(int Id, int SourceNode, string SourcePort, string ThisPort)> inparams = [];
        private readonly List<int> outflowConnectionIds = [];

        public IEnumerable<int> OutflowTargets => outflows.Select(outflow => outflow.Target);

        public bool ReadsFrom(int nodeId) => inparams.Any(inparam => inparam.SourceNode == nodeId);

        public IEnumerable<(int Id, string SourcePort)> InparamConnectionsFrom(int nodeId)
            => inparams.Where(inparam => inparam.SourceNode == nodeId).Select(inparam => (inparam.Id, inparam.SourcePort));

        public void RenumberInparamConnections(Dictionary<int, int> newIds)
        {
            for (var i = 0; i < inparams.Count; i++)
            {
                if (newIds.TryGetValue(inparams[i].Id, out var newId))
                {
                    inparams[i] = inparams[i] with { Id = newId };
                }
            }
        }

        public void AddOutflowConnection(int connectionId, string outflow, int target, string inflow)
        {
            if (outflows.Contains((outflow, target, inflow)))
            {
                return;
            }

            outflows.Add((outflow, target, inflow));
            outflowConnectionIds.Add(connectionId);
        }

        public void AddInparamConnection(int connectionId, int sourceNode, string sourcePort, string thisPort)
        {
            inparams.RemoveAll(inparam => inparam.ThisPort == thisPort);
            inparams.Add((connectionId, sourceNode, sourcePort, thisPort));
        }

        public KVObject ToKeyValues()
        {
            var node = KVObject.Collection();
            node.Add("_class", ClassName);
            node.Add("m_NodeID", Id);

            if (Name != null)
            {
                node.Add("m_Name", Name);
            }

            node.Add("m_vPosition", KVObject.Array([Position.X, Position.Y]));

            foreach (var (key, value) in Fields)
            {
                node.Add(key, value);
            }

            var children = KVObject.Array();

            foreach (var (index, (outflow, target, inflow)) in outflows.Index())
            {
                var connection = KVObject.Collection();
                connection.Add("_class", "Connection_Outflow");
                connection.Add("m_NodeID", outflowConnectionIds[index]);
                connection.Add("m_SourceOutflowName", outflow);
                connection.Add("m_DestInflowName", inflow);
                connection.Add("m_DestFlowNodeID", target);
                children.Add(connection);
            }

            foreach (var (connectionId, sourceNode, sourcePort, thisPort) in inparams)
            {
                var connection = KVObject.Collection();
                connection.Add("_class", "Connection_Inparam");
                connection.Add("m_NodeID", connectionId);
                connection.Add("m_SourceNode", sourceNode);
                connection.Add("m_SourceOutparamName", sourcePort);
                connection.Add("m_ThisInparamName", thisPort);
                children.Add(connection);
            }

            if (children.Count > 0)
            {
                node.Add("m_Children", children);
            }

            return node;
        }
    }

    private DocNode AddNode(int id, string className, bool isFlow)
    {
        var node = new DocNode(id, className) { IsFlow = isFlow };
        nodes.Add(id, node);
        nodeOrder.Add(node);
        usedNodeIds.Add(id);
        return node;
    }

    private void ConnectFlow(int sourceId, string outflow, int targetId, string arrivalSequencePoint)
    {
        if (!nodes.TryGetValue(sourceId, out var source) || !nodes.TryGetValue(targetId, out var target))
        {
            pendingFlowConnections.Add((sourceId, outflow, targetId, arrivalSequencePoint));
            return;
        }

        var inflow = target.InflowName ?? (IsSequencePointName(arrivalSequencePoint) ? target.FallbackInflowName : arrivalSequencePoint);
        source.AddOutflowConnection(AllocateNodeId(), outflow, targetId, inflow);
    }

    // Connections made before the target node knew its type, resolved once the walk is done
    private readonly List<(int Source, string Outflow, int Target, string ArrivalSequencePoint)> pendingFlowConnections = [];

    private void ResolvePendingFlowConnections()
    {
        var pending = pendingFlowConnections.ToList();
        pendingFlowConnections.Clear();

        foreach (var nodeId in pending.SelectMany(connection => new[] { connection.Source, connection.Target }).Distinct())
        {
            if (!nodes.ContainsKey(nodeId))
            {
                TryAddSkippedEntFire(nodeId);
            }
        }

        // A node that compiled to nothing, such as a skipped one, is left out and the flow through it is joined up
        var passedThrough = pending.Where(connection => !nodes.ContainsKey(connection.Source)).ToLookup(connection => connection.Source);

        IEnumerable<(int Target, string Arrival)> ResolveTargets(int target, string arrival, HashSet<int> seen)
        {
            if (nodes.ContainsKey(target))
            {
                return [(target, arrival)];
            }

            if (!seen.Add(target))
            {
                return [];
            }

            Report($"Node {target} compiled to no instructions, it is left out of the document.");
            return passedThrough[target].SelectMany(next => ResolveTargets(next.Target, next.ArrivalSequencePoint, seen)).ToList();
        }

        foreach (var (source, outflow, target, arrival) in pending)
        {
            if (!nodes.ContainsKey(source))
            {
                continue;
            }

            foreach (var (resolvedTarget, resolvedArrival) in ResolveTargets(target, arrival, []))
            {
                ConnectFlow(source, outflow, resolvedTarget, resolvedArrival);
            }
        }
    }

    /// <summary>
    /// A skipped node compiles to nothing, but a skipped entity fire still lists the output connection it makes, which is
    /// enough to bring it back. Its parameter value is not kept.
    /// </summary>
    private void TryAddSkippedEntFire(int nodeId)
    {
        var sourceOutput = $"Step_EntFire:{nodeId}";
        var outputConnection = (graph.GetArray("m_OutputConnections") ?? [])
            .FirstOrDefault(connection => connection.GetStringProperty("m_SourceOutput", string.Empty).EndsWith(sourceOutput, StringComparison.Ordinal));

        if (outputConnection == null)
        {
            return;
        }

        // The only sequence point left of the node is its outflow, so the cursor seems to arrive there
        var node = AddNode(nodeId, "Step_EntFire", isFlow: true);
        node.InflowName = "InDefault";

        var target = KVObject.Collection();
        target.Add("m_ValueSource", "domain_value");
        target.Add("m_LiteralValue", outputConnection["m_TargetEntity"]);
        node.Fields.Add("m_TargetName", target);
        node.Fields.Add("m_Input", outputConnection.GetStringProperty("m_TargetInput", string.Empty));

        if (!string.IsNullOrEmpty(outputConnection.GetStringProperty("m_Param", string.Empty)))
        {
            var param = KVObject.Collection();
            param.Add("m_ValueSource", "literal");
            param.Add("m_LiteralValue", string.Empty);
            node.Fields.Add("m_Param", param);
            node.Fields.Add("m_SingleParamType", "PVAL_STRING");
            Report($"Node {nodeId} is a skipped entity fire, its parameter value is not kept.");
        }

        node.Fields.Add("m_bNodeSkip", true);
    }

    /// <summary>
    /// Sequence points mark where in a node the cursor is: its entry or step points, as opposed to the named outflows it
    /// leaves through.
    /// </summary>
    private static bool IsSequencePointName(string? name)
        => string.IsNullOrEmpty(name) || name == "InDefault" || name.Contains("Point", StringComparison.Ordinal);
    #endregion
}
