using System.Globalization;
using System.Linq;
using ValveKeyValue;
using ValveResourceFormat.Serialization.KeyValues;

namespace ValveResourceFormat.IO;

public sealed partial class PulseGraphExtract
{
    /// <summary>One cursor flow being followed through a chunk.</summary>
    private sealed class FlowPath
    {
        public required int Chunk { get; set; }
        public required int Instruction { get; set; }
        public required Dictionary<int, ValueRef> Registers { get; set; }

        // The editor node the instructions currently being walked belong to
        public required int CurrentNode { get; set; }

        public string? LastSequencePoint { get; set; }

        // Outflow taken when leaving the current node if no sequence point names one, e.g. the branch of an If
        public string? DefaultOutflow { get; set; }

        public FlowPath Fork(int chunk, int instruction, string? defaultOutflow) => new()
        {
            Chunk = chunk,
            Instruction = instruction,
            Registers = chunk == Chunk ? new Dictionary<int, ValueRef>(Registers) : [],
            CurrentNode = CurrentNode,
            LastSequencePoint = LastSequencePoint,
            DefaultOutflow = defaultOutflow,
        };
    }

    private readonly Queue<FlowPath> pathQueue = [];
    private readonly HashSet<int> enteredFlowNodes = [];
    private readonly Dictionary<int, HashSet<string>> arrivalSequencePoints = [];
    private readonly HashSet<int> fireAndForgetNodes = [];

    // Instructions that fork the flow, followed once no matter how many paths reach them, e.g. through a loop's back edge
    private readonly HashSet<(int Chunk, int Instruction)> forkingInstructions = [];

    // Loop nodes whose body reads the loop index from this register
    private readonly Dictionary<int, int> loopIndexRegisters = [];

    // Outflows each node runs through calls, which is all a simultaneous outflow compiles to
    private readonly Dictionary<int, List<string>> outflowCallPorts = [];

    // Registers of entry nodes with several outparams, which the compiler numbers in the order of the connections reading them
    private readonly Dictionary<int, Dictionary<string, int>> entryOutparamRegisters = [];

    private readonly Dictionary<int, HashSet<int>> nonVariableValueNodes = [];

    // Expression nodes by the value node their instructions were compiled under
    private readonly Dictionary<int, int> expressionOwners = [];
    private readonly Dictionary<(int Expression, int Part), int> renumberedExpressionParts = [];

    private void AddEntryNode(int cellIndex, KVObject cell)
    {
        var id = cellNodeIds[cellIndex];
        var className = cell.GetStringProperty("_class", string.Empty);
        DocNode node;

        switch (className)
        {
            case "CPulseCell_Inflow_Method":
                node = AddNode(id, "Inflow_Method", isFlow: true);
                node.Name = cell.GetStringProperty("m_MethodName", string.Empty);
                node.Fields.Add("m_bIsPublic", cell.GetBooleanProperty("m_bIsPublic"));
                node.Fields.Add("m_Description", cell.GetStringProperty("m_Description", string.Empty));
                node.Fields.Add("m_Args", BuildMethodArgs(cell.GetArray("m_Args")));
                node.Fields.Add("m_ReturnValues", BuildMethodArgs(cell.GetArray("m_ReturnValues")));
                break;

            case "CPulseCell_Inflow_EventHandler":
                node = AddGenericNode(id, cell.GetStringProperty("m_EventName", string.Empty), isFlow: true);
                break;

            case "CPulseCell_Inflow_GraphHook":
                node = AddGenericNode(id, cell.GetStringProperty("m_HookName", string.Empty), isFlow: true);
                break;

            case "CPulseCell_Inflow_EntOutputHandler":
                node = AddNode(id, "Inflow_EntOutput", isFlow: true);
                node.Fields.Add("m_SourceEntity", cell.GetStringProperty("m_SourceEntity", string.Empty));
                node.Fields.Add("m_SourceOutput", cell.GetStringProperty("m_SourceOutput", string.Empty));
                node.Fields.Add("m_CachedOutputType", cell.GetStringProperty("m_ExpectedParamType", "PVAL_VOID"));
                break;

            default:
                node = AddGenericNode(id, className, isFlow: true);
                node.Fields.Add("m_GenericKeys", GetGenericKeys(cell));
                break;
        }

        node.IsEntry = true;

        var registers = new Dictionary<int, ValueRef>();
        AddOutparamRegisters(registers, cell, id);

        var outparamRegisters = registers.ToDictionary(register => ((NodeOutput)register.Value).Port, register => register.Key);
        if (outparamRegisters.Count > 1)
        {
            entryOutparamRegisters[id] = outparamRegisters;
        }

        enteredFlowNodes.Add(id);
        pathQueue.Enqueue(new FlowPath
        {
            Chunk = cell.GetInt32Property("m_EntryChunk"),
            Instruction = 0,
            Registers = registers,
            CurrentNode = id,
        });
    }

    private DocNode AddGenericNode(int id, string nodeType, bool isFlow)
    {
        var node = AddNode(id, isFlow ? "Flow_Generic" : "Value_Generic", isFlow);
        node.Fields.Add("m_GenericNodeType", nodeType);
        node.Fields.Add("m_GenericPorts", KVObject.Collection());
        return node;
    }

    // Cell settings the editor keeps as a custom attribute, stored as a table holding the setting under the same name,
    // such as the output an entity output listener waits for
    private static readonly HashSet<string> CustomAttributeKeys = ["m_strEntityOutput"];

    // The settings of a cell, leaving out the bookkeeping the compiler fills in
    private static KVObject GetGenericKeys(KVObject cell)
    {
        var keys = KVObject.Collection();

        foreach (var (key, value) in cell)
        {
            if (key is "_class" or "m_nEditorNodeID" or "m_EntryChunk" or "m_RegisterMap" || IsOutflowData(value))
            {
                continue;
            }

            if (CustomAttributeKeys.Contains(key))
            {
                var attribute = KVObject.Collection();
                attribute.Add(key, value);
                keys.Add(key, attribute);
                continue;
            }

            keys.Add(key, value);
        }

        return keys;
    }

    private static bool IsOutflowData(KVObject value)
    {
        if (value.IsCollection)
        {
            return value.ContainsKey("m_SourceOutflowName") || value.Any(child => IsOutflowData(child.Value));
        }

        if (value.IsArray)
        {
            return value.AsArraySpan().ToArray().Any(IsOutflowData);
        }

        return false;
    }

    private static void AddOutparamRegisters(Dictionary<int, ValueRef> registers, KVObject owner, int nodeId)
    {
        if (!owner.TryGetValue("m_RegisterMap", out var registerMap))
        {
            return;
        }

        AddOutparamRegistersFromMap(registers, registerMap, nodeId);
    }

    private static void AddOutparamRegistersFromMap(Dictionary<int, ValueRef> registers, KVObject registerMap, int nodeId)
    {
        if (registerMap.IsNull || !registerMap.TryGetValue("m_Outparams", out var outParams) || outParams.IsNull)
        {
            return;
        }

        foreach (var (name, register) in outParams)
        {
            registers[(int)register] = new NodeOutput(nodeId, name);
        }
    }

    private void WalkAllPaths()
    {
        while (pathQueue.TryDequeue(out var path))
        {
            WalkPath(path);
        }

        foreach (var (nodeId, ports) in outflowCallPorts)
        {
            if (!nodes.ContainsKey(nodeId))
            {
                var node = AddNode(nodeId, "Outflow_Simultaneous", isFlow: true);
                node.Fields.Add("m_nNumExits", ports.Count);
            }
        }

        ResolvePendingFlowConnections();
    }

    private void WalkPath(FlowPath path)
    {
        var visited = new HashSet<(int Chunk, int Instruction)>();

        while (true)
        {
            var instructions = chunks[path.Chunk].GetArray("m_Instructions");

            if (path.Instruction < 0 || path.Instruction >= instructions.Count || !visited.Add((path.Chunk, path.Instruction)))
            {
                return;
            }

            var (flowNode, valueNode, sequencePoint) = GetDebugInfo(path.Chunk, path.Instruction);

            if (flowNode >= 0 && flowNode != path.CurrentNode)
            {
                ConnectFlow(path.CurrentNode, GetLeavingOutflow(path), flowNode, sequencePoint ?? string.Empty);

                if (!enteredFlowNodes.Add(flowNode))
                {
                    return;
                }

                path.CurrentNode = flowNode;
                path.LastSequencePoint = null;
                path.DefaultOutflow = null;

                if (!IsSequencePointName(sequencePoint))
                {
                    GetArrivalSequencePoints(flowNode).Add(sequencePoint!);
                }
            }

            if (sequencePoint != null)
            {
                path.LastSequencePoint = sequencePoint;
            }

            if (!ExecuteInstruction(path, instructions[path.Instruction], valueNode, sequencePoint))
            {
                return;
            }
        }
    }

    private HashSet<string> GetArrivalSequencePoints(int nodeId)
    {
        if (!arrivalSequencePoints.TryGetValue(nodeId, out var points))
        {
            points = [];
            arrivalSequencePoints[nodeId] = points;
        }

        return points;
    }

    private string GetLeavingOutflow(FlowPath path)
    {
        var last = path.LastSequencePoint;

        if (!IsSequencePointName(last) && !GetArrivalSequencePoints(path.CurrentNode).Contains(last!))
        {
            return last!;
        }

        return path.DefaultOutflow ?? "OutDefault";
    }

    private (int FlowNode, int ValueNode, string? SequencePoint) GetDebugInfo(int chunk, int instruction)
    {
        var debugInfos = chunks[chunk].GetArray("m_InstructionDebugInfos");
        if (debugInfos == null || instruction >= debugInfos.Count)
        {
            return (-1, -1, null);
        }

        var debugInfo = debugInfos[instruction];
        return (
            debugInfo.GetInt32Property("m_nFlowNodeID", -1),
            debugInfo.GetInt32Property("m_nValueNodeID", -1),
            debugInfo.GetStringProperty("m_SequencePointName", string.Empty));
    }

    private ValueRef GetRegister(FlowPath path, int register)
    {
        if (path.Registers.TryGetValue(register, out var value))
        {
            return value;
        }

        Report($"Register {register} in chunk {path.Chunk} is read before anything on the walked flow writes it.");
        return new UnknownValue();
    }

    private KVObject? GetRegisterInfo(int chunk, int register)
        => chunks[chunk].GetArray("m_Registers")?.FirstOrDefault(info => info.GetInt32Property("m_nReg", -1) == register);

    private int GetRegisterWriteInstruction(int chunk, int register)
        => GetRegisterInfo(chunk, register)?.GetInt32Property("m_nWrittenByInstruction", -1) ?? -1;

    private string GetRegisterType(int chunk, int register)
        => GetRegisterInfo(chunk, register)?.GetStringProperty("m_Type", "PVAL_INVALID") ?? "PVAL_INVALID";

    // The node that computed a register, and which of its ports it came from
    private bool TryGetRegisterOrigin(int chunk, int register, out int nodeId, out string port)
    {
        var originName = GetRegisterInfo(chunk, register)?.GetStringProperty("m_OriginName", string.Empty) ?? string.Empty;
        return TryParseOriginName(originName, out nodeId, out port);
    }

    private int FindRegisterByOrigin(int chunk, int nodeId, string port)
    {
        foreach (var register in chunks[chunk].GetArray("m_Registers") ?? [])
        {
            if (TryParseOriginName(register.GetStringProperty("m_OriginName", string.Empty), out var originNode, out var originPort)
                && originNode == nodeId && originPort == port)
            {
                return register.GetInt32Property("m_nReg", -1);
            }
        }

        return -1;
    }

    /// <returns>Whether the flow continues past this instruction.</returns>
    private bool ExecuteInstruction(FlowPath path, KVObject instruction, int valueNode, string? sequencePoint)
    {
        var code = instruction.GetStringProperty("m_nCode", string.Empty);
        var reg0 = instruction.GetInt32Property("m_nReg0", -1);
        var reg1 = instruction.GetInt32Property("m_nReg1", -1);
        var reg2 = instruction.GetInt32Property("m_nReg2", -1);

        switch (code)
        {
            case "NOP":
            case "DETACH_REGISTER":
                break;

            case "IMMEDIATE_HALT":
            case "RETURN_VOID":
            case "RETURN_VALUE":
                return false;

            case "JUMP":
                path.Instruction = instruction.GetInt32Property("m_nDestInstruction");
                return true;

            case "CHUNK_LEAP":
                path.Chunk = instruction.GetInt32Property("m_nChunk");
                path.Instruction = 0;
                path.Registers = [];
                return true;

            case "GET_CONST":
            {
                var constant = constants[instruction.GetInt32Property("m_nConstIdx")];
                path.Registers[reg0] = new LiteralValue(constant["m_Value"], constant.GetStringProperty("m_Type", "PVAL_INVALID"));
                break;
            }

            case "GET_DOMAIN_VALUE":
                path.Registers[reg0] = new DomainValue(domainValues[instruction.GetInt32Property("m_nDomainValueIdx")]["m_Value"]);
                break;

            case "GET_VAR":
            case "GET_VAR_DETACH":
                path.Registers[reg0] = ReadVariable(path, variableNodeIds[instruction.GetInt32Property("m_nVar")], valueNode);
                break;

            // Casts are inserted by the compiler wherever types differ, the document does not spell them out
            case "CONVERT_VALUE":
            case "REINTERPRET_INSTANCE":
            case "COPY":
                path.Registers[reg0] = GetRegister(path, reg1);
                break;

            case "NOT":
            case "NEGATE":
            case "ADD":
            case "SUB":
            case "MUL":
            case "DIV":
            case "MOD":
            case "LT":
            case "LTE":
            case "EQ":
            case "NE":
            case "AND":
            case "OR":
            case "SCALE":
            case "SCALE_INV":
                AddExpression(path, code, reg0, reg1, reg2, valueNode);
                break;

            case "LIBRARY_INVOKE":
                AddLibraryInvoke(path, instruction, valueNode);
                break;

            case "CELL_INVOKE":
                AddCellInvoke(path, instruction, valueNode);
                break;

            case "PULSE_CALL_SYNC":
            case "PULSE_CALL_ASYNC_FIRE":
                AddMethodCall(path, instruction, code == "PULSE_CALL_ASYNC_FIRE");
                break;

            case "CREATE_CHILD_CURSOR_OUTFLOW":
                AddChildCursor(path, instruction, sequencePoint);
                break;

            case "SET_VAR":
            case "SET_VAR_OBSERVABLE":
                AddSetVariable(path, instruction, code == "SET_VAR_OBSERVABLE");
                break;

            case "GET_TEMPVAR":
                path.Registers[reg0] = new LocalNameValue(GetTempVar(path.Chunk, instruction)?.GetStringProperty("m_Name", string.Empty) ?? string.Empty);
                break;

            case "SET_TEMPVAR":
            case "SET_TEMPVAR_OBSERVABLE":
                AddSetTempVar(path, instruction);
                break;

            case "LOOP_BREAK":
                if (!nodes.ContainsKey(path.CurrentNode))
                {
                    AddNode(path.CurrentNode, "Outflow_Break", isFlow: true);
                }

                return false;

            case "JUMP_COND":
            case "CHUNK_LEAP_COND":
                return AddBranch(path, instruction, valueNode, sequencePoint, code == "CHUNK_LEAP_COND");

            default:
                Report($"Instruction {code} is not supported yet (chunk {path.Chunk}, instruction {path.Instruction}).");
                break;
        }

        path.Instruction++;
        return true;
    }

    // A variable read through its own Var Reference node, rather than inline on the port, compiles under that node's id
    private ValueRef ReadVariable(FlowPath path, int variableNodeId, int valueNode)
    {
        if (valueNode < 0 || valueNode == path.CurrentNode || GetNonVariableValueNodes(path.Chunk).Contains(valueNode))
        {
            return new VariableValue(variableNodeId);
        }

        if (!nodes.ContainsKey(valueNode))
        {
            var node = AddNode(valueNode, "Value_VarReference", isFlow: false);
            node.Fields.Add("m_SourceID", variableNodeId);
        }

        return new NodeOutput(valueNode, "value");
    }

    // Value nodes that compiled to something other than variable reads, such as text expressions reading variables
    private HashSet<int> GetNonVariableValueNodes(int chunk)
    {
        if (!nonVariableValueNodes.TryGetValue(chunk, out var valueNodes))
        {
            valueNodes = [];
            var instructions = chunks[chunk].GetArray("m_Instructions");

            for (var index = 0; index < instructions.Count; index++)
            {
                var code = instructions[index].GetStringProperty("m_nCode", string.Empty);
                var (_, valueNode, _) = GetDebugInfo(chunk, index);

                if (valueNode >= 0 && code is not ("GET_VAR" or "GET_VAR_DETACH"))
                {
                    valueNodes.Add(valueNode);
                }
            }

            nonVariableValueNodes[chunk] = valueNodes;
        }

        return valueNodes;
    }

    private static string GetExpressionClass(string code) => code switch
    {
        "NOT" => "ExprNot",
        "NEGATE" => "ExprNegate",
        "ADD" => "ExprAdd",
        "SUB" => "ExprSub",
        "MUL" or "SCALE" => "ExprMul",
        "DIV" or "SCALE_INV" => "ExprDiv",
        "MOD" => "ExprMod",
        "LT" => "ExprLess",
        "LTE" => "ExprLessEq",
        "EQ" => "ExprEqual",
        "NE" => "ExprNotEqual",
        "AND" => "ExprAnd",
        "OR" => "ExprOr",
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, null),
    };

    private void AddExpression(FlowPath path, string code, int reg0, int reg1, int reg2, int valueNode)
    {
        var hasOrigin = TryGetRegisterOrigin(path.Chunk, reg0, out var nodeId, out var port);

        // Flow nodes such as loops and switches compute their own conditions
        if (hasOrigin && nodeId == path.CurrentNode)
        {
            ValueRef value = (code, port) switch
            {
                ("NOT", "not_condition") => new InvertedValue(GetRegister(path, reg1)),
                ("EQ", "__case_cond") => new CaseCondition(GetRegister(path, reg1), GetRegister(path, reg2), GetRegisterType(path.Chunk, reg1)),
                _ => new InternalValue(port, reg1 >= 0 ? GetRegister(path, reg1) : null, reg2 >= 0 ? GetRegister(path, reg2) : null),
            };

            path.Registers[reg0] = value;
            return;
        }

        if (!hasOrigin)
        {
            nodeId = valueNode >= 0 && valueNode != path.CurrentNode ? valueNode : AllocateNodeId();
            port = "retval";
        }

        // Parts of a text expression are numbered within their expression, so they can clash with other nodes
        if (renumberedExpressionParts.TryGetValue((valueNode, nodeId), out var renumbered))
        {
            nodeId = renumbered;
        }
        else if (nodes.ContainsKey(nodeId) && expressionOwners.GetValueOrDefault(nodeId, valueNode) != valueNode)
        {
            renumbered = AllocateNodeId();
            renumberedExpressionParts[(valueNode, nodeId)] = renumbered;
            nodeId = renumbered;
        }

        if (!nodes.ContainsKey(nodeId))
        {
            var className = GetExpressionClass(code);

            // Greater-than compiles to less-than with its operands swapped, but its first operand is still evaluated first
            if (code is "LT" or "LTE" && GetRegisterWriteInstruction(path.Chunk, reg2) < GetRegisterWriteInstruction(path.Chunk, reg1))
            {
                className = code == "LT" ? "ExprGreater" : "ExprGreaterEq";
                (reg1, reg2) = (reg2, reg1);
            }

            var node = AddNode(nodeId, className, isFlow: false);
            expressionOwners[nodeId] = valueNode;

            if (code is "NOT" or "NEGATE")
            {
                SetInputPort(node, node.Fields, "m_Arg", "value", GetRegister(path, reg1), refineLiteralType: true);
            }
            else
            {
                SetInputPort(node, node.Fields, "m_FirstArg", "first", GetRegister(path, reg1), refineLiteralType: true);
                SetInputPort(node, node.Fields, "m_SecondArg", "second", GetRegister(path, reg2), refineLiteralType: true);
            }
        }

        path.Registers[reg0] = new NodeOutput(nodeId, port);
    }

    private void SetRegisterMapPorts(FlowPath path, DocNode node, KVObject container, KVObject registerMap)
    {
        if (registerMap.IsNull || !registerMap.TryGetValue("m_Inparams", out var inParams) || inParams.IsNull)
        {
            return;
        }

        foreach (var (name, register) in inParams)
        {
            // A literal passed to a port of any type has to say which type it is
            var portType = GetRegisterType(path.Chunk, (int)register);
            var takesAnyType = portType.StartsWith("PVAL_VARIANT", StringComparison.Ordinal) || portType.StartsWith("PVAL_ANY", StringComparison.Ordinal);

            SetInputPort(node, container, name, name, GetRegister(path, (int)register), refineLiteralType: takesAnyType);
        }
    }

    private void AddLibraryInvoke(FlowPath path, KVObject instruction, int valueNode)
    {
        var binding = invokeBindings[instruction.GetInt32Property("m_nInvokeBindingIndex")];
        var registerMap = binding["m_RegisterMap"];
        var isValue = valueNode >= 0 && valueNode != path.CurrentNode;
        var nodeId = isValue ? valueNode : path.CurrentNode;

        if (!nodes.ContainsKey(nodeId))
        {
            var node = AddGenericNode(nodeId, binding.GetStringProperty("m_FuncName", string.Empty), !isValue);
            SetRegisterMapPorts(path, node, node.Fields["m_GenericPorts"], registerMap);
        }

        AddOutparamRegisters(path.Registers, binding, nodeId);
    }

    private void AddCellInvoke(FlowPath path, KVObject instruction, int valueNode)
    {
        var binding = invokeBindings[instruction.GetInt32Property("m_nInvokeBindingIndex")];
        var cellIndex = binding.GetInt32Property("m_nCellIndex");
        var cell = cells[cellIndex];
        var nodeId = cellNodeIds[cellIndex];
        var isValue = nodeId != path.CurrentNode && valueNode == nodeId;

        if (!nodes.ContainsKey(nodeId))
        {
            AddCellNode(path, cell, nodeId, binding, isValue);

            if (!isValue)
            {
                QueueCellOutflows(path, cell, nodeId);
            }
        }

        AddOutparamRegisters(path.Registers, binding, nodeId);
    }

    private void AddCellNode(FlowPath path, KVObject cell, int nodeId, KVObject binding, bool isValue)
    {
        var className = cell.GetStringProperty("_class", string.Empty);
        var registerMap = binding["m_RegisterMap"];
        var inParams = registerMap.IsNull ? null : registerMap["m_Inparams"];
        DocNode node;

        bool HasInParam(string name) => inParams != null && !inParams.IsNull && inParams.ContainsKey(name);

        ValueRef InParam(string name) => HasInParam(name) ? GetRegister(path, (int)inParams![name]) : new UnknownValue();

        switch (className)
        {
            case "CPulseCell_Step_EntFire":
                node = AddNode(nodeId, "Step_EntFire", isFlow: true);

                if (HasInParam("hTarget"))
                {
                    node.Fields.Add("m_nFireMode", "PulseEntFireMode_t::HANDLE");
                    SetInputPort(node, node.Fields, "m_TargetHandle", "hTarget", InParam("hTarget"));
                }

                if (HasInParam("TargetName"))
                {
                    SetInputPort(node, node.Fields, "m_TargetName", "TargetName", InParam("TargetName"));
                }

                node.Fields.Add("m_Input", cell.GetStringProperty("m_Input", string.Empty));

                if (HasInParam("pParam"))
                {
                    SetInputPort(node, node.Fields, "m_Param", "pParam", InParam("pParam"));
                    node.Fields.Add("m_SingleParamType", GetRegisterType(path.Chunk, (int)inParams!["pParam"]));
                }

                break;

            case "CPulseCell_Step_DebugLog":
                node = AddNode(nodeId, "Step_DebugLog", isFlow: true);
                SetInputPort(node, node.Fields, "m_Message", "pMessage", InParam("pMessage"));
                break;

            case "CPulseCell_Step_PublicOutput":
            {
                node = AddNode(nodeId, "Step_PublicOutput", isFlow: true);
                var outputIndex = cell.GetInt32Property("m_OutputIndex", -1);
                node.Fields.Add("m_OutputDefID", outputIndex >= 0 && outputIndex < publicOutputNodeIds.Length ? publicOutputNodeIds[outputIndex] : -1);
                var argList = KVObject.Collection();
                node.Fields.Add("m_ArgList", argList);
                SetRegisterMapPorts(path, node, argList, registerMap);
                break;
            }

            case "CPulseCell_ReturnValues":
            {
                node = AddNode(nodeId, "Outflow_Return", isFlow: true);
                var returnValues = KVObject.Collection();
                node.Fields.Add("m_ReturnValues", returnValues);
                SetRegisterMapPorts(path, node, returnValues, registerMap);
                break;
            }

            case "CPulseCell_FireCursors":
                node = AddNode(nodeId, "Step_FireCursors", isFlow: true);
                node.Fields.Add("m_nChildCursors", cell.GetInt32Property("m_nChildCursors"));
                node.InflowName = "Start";
                break;

            case "CPulseCell_Timeline":
            {
                node = AddNode(nodeId, "Step_Timeline", isFlow: true);
                node.InflowName = "Start";

                // Events left unconnected lose their port name, the editor names them by position
                var events = KVObject.Array();
                foreach (var (index, timelineEvent) in (cell.GetArray("m_TimelineEvents") ?? []).Index())
                {
                    var portName = timelineEvent.GetSubCollection("m_EventOutflow")?.GetStringProperty("m_SourceOutflowName", string.Empty);
                    var definition = KVObject.Collection();
                    definition.Add("m_InternalPortName", string.IsNullOrEmpty(portName) ? $"event_{index}" : portName);
                    definition.Add("m_FriendlyName", string.Empty);
                    definition.Add("m_flTimeFromPrevious", timelineEvent.GetFloatProperty("m_flTimeFromPrevious"));
                    events.Add(definition);
                }

                node.Fields.Add("m_EventDefinitions", events);
                node.Fields.Add("m_bWaitForChildOutflows", cell.GetBooleanProperty("m_bWaitForChildOutflows"));
                break;
            }

            case "CPulseCell_Outflow_CycleOrdered":
            case "CPulseCell_Outflow_CycleShuffled":
            case "CPulseCell_Outflow_CycleRandom":
                node = AddNode(nodeId, "Outflow_Cycle", isFlow: true);
                node.Fields.Add("m_nNumExits", cell.GetArray("m_Outputs")?.Count ?? 0);
                node.Fields.Add("m_nCycleMode", className switch
                {
                    "CPulseCell_Outflow_CycleShuffled" => "PulseCycleMode_t::SHUFFLED",
                    "CPulseCell_Outflow_CycleRandom" => "PulseCycleMode_t::RANDOM",
                    _ => "PulseCycleMode_t::ORDERED",
                });
                break;

            default:
            {
                node = AddGenericNode(nodeId, className, !isValue);
                node.Fields.Add("m_GenericKeys", GetGenericKeys(cell));
                SetRegisterMapPorts(path, node, node.Fields["m_GenericPorts"], registerMap);

                // Generic cells are entered through the method the invoke binding names
                var funcName = binding.GetStringProperty("m_FuncName", string.Empty);
                var separator = funcName.LastIndexOf("::", StringComparison.Ordinal);
                if (separator >= 0)
                {
                    node.FallbackInflowName = funcName[(separator + 2)..];
                }

                break;
            }
        }
    }

    private void QueueCellOutflows(FlowPath path, KVObject cell, int nodeId)
    {
        foreach (var outflow in FindOutflows(cell))
        {
            var destChunk = outflow.GetInt32Property("m_nDestChunk", -1);
            var destInstruction = outflow.GetInt32Property("m_nInstruction", -1);
            if (destChunk < 0 || destInstruction < 0)
            {
                continue;
            }

            var outflowPath = path.Fork(destChunk, destInstruction, outflow.GetStringProperty("m_SourceOutflowName", "OutDefault"));
            outflowPath.CurrentNode = nodeId;

            if (outflow.TryGetValue("m_OutflowRegisterMap", out var outflowRegisterMap))
            {
                AddOutparamRegistersFromMap(outflowPath.Registers, outflowRegisterMap, nodeId);
            }

            pathQueue.Enqueue(outflowPath);
        }
    }

    // Every outflow connection of a cell, found by shape anywhere in its data
    private static List<KVObject> FindOutflows(KVObject cell)
    {
        var outflows = new List<KVObject>();

        void Visit(KVObject value)
        {
            if (value.IsCollection)
            {
                if (value.ContainsKey("m_SourceOutflowName") && value.ContainsKey("m_nDestChunk"))
                {
                    outflows.Add(value);
                    return;
                }

                foreach (var (_, child) in value)
                {
                    Visit(child);
                }
            }
            else if (value.IsArray)
            {
                foreach (var element in value.AsArraySpan())
                {
                    Visit(element);
                }
            }
        }

        Visit(cell);
        return outflows;
    }

    private void AddMethodCall(FlowPath path, KVObject instruction, bool isAsync)
    {
        var callInfo = callInfos[instruction.GetInt32Property("m_nCallInfoIndex")];
        var targetChunk = instruction.GetInt32Property("m_nChunk");

        // A call that targets no method runs one of the node's outflows and comes back, e.g. each iteration of a loop
        // or each exit of a simultaneous outflow. Flows reached from several places are compiled into a chunk of their own.
        if (callInfo.GetInt32Property("m_CallMethodID", -1) < 0)
        {
            if (!forkingInstructions.Add((path.Chunk, path.Instruction)))
            {
                return;
            }

            var ownerId = callInfo.GetInt32Property("m_nEditorNodeID", -1);
            if (ownerId < 0)
            {
                ownerId = path.CurrentNode;
            }

            // Calls made from a sequence point rather than a port carry on through the node's usual outflow
            var portName = callInfo.GetStringProperty("m_PortName", "OutDefault");
            var isPortless = portName.EndsWith(":no_port", StringComparison.Ordinal);
            var body = path.Fork(targetChunk, instruction.GetInt32Property("m_nDestInstruction"), isPortless ? path.DefaultOutflow : portName);
            body.CurrentNode = ownerId;
            body.LastSequencePoint = null;

            if (targetChunk == path.Chunk && loopIndexRegisters.TryGetValue(ownerId, out var indexRegister))
            {
                body.Registers[indexRegister] = new NodeOutput(ownerId, "Index");
            }

            if (!isPortless)
            {
                if (!outflowCallPorts.TryGetValue(ownerId, out var ports))
                {
                    ports = [];
                    outflowCallPorts[ownerId] = ports;
                }

                ports.Add(portName);
            }

            pathQueue.Enqueue(body);
            return;
        }

        var nodeId = callInfo.GetInt32Property("m_nEditorNodeID", -1);
        if (nodeId < 0)
        {
            nodeId = path.CurrentNode >= 0 ? path.CurrentNode : AllocateNodeId();
        }

        if (!nodes.ContainsKey(nodeId))
        {
            var node = AddNode(nodeId, "Step_CallMethod", isFlow: true);
            node.Fields.Add("m_MethodID", GetCalledMethodNodeId(callInfo, targetChunk));

            if (isAsync)
            {
                node.Fields.Add("m_nCallMode", "ASYNC_FIRE_AND_FORGET");
            }

            var argList = KVObject.Collection();
            node.Fields.Add("m_ArgList", argList);
            SetRegisterMapPorts(path, node, argList, callInfo["m_RegisterMap"]);
        }

        AddOutparamRegisters(path.Registers, callInfo, nodeId);
    }

    private int GetCalledMethodNodeId(KVObject callInfo, int targetChunk)
    {
        var methodId = callInfo.GetInt32Property("m_CallMethodID", -1);
        if (methodId >= 0)
        {
            return methodId;
        }

        foreach (var (cellIndex, cell) in cells.Index())
        {
            if (cell.GetStringProperty("_class", string.Empty) == "CPulseCell_Inflow_Method" && cell.GetInt32Property("m_EntryChunk", -1) == targetChunk)
            {
                return cellNodeIds[cellIndex];
            }
        }

        Report($"Could not find the method called into chunk {targetChunk}.");
        return -1;
    }

    // A yielding node set to not wait for completion runs in a child cursor while the flow carries on past it
    private void AddChildCursor(FlowPath path, KVObject instruction, string? sequencePoint)
    {
        var destChunk = instruction.GetInt32Property("m_nChunk");
        var destInstruction = instruction.GetInt32Property("m_nDestInstruction");

        if (sequencePoint != "m_OptionalFireAndForgetPoint")
        {
            Report($"Child cursor at sequence point '{sequencePoint}' is not supported yet (chunk {path.Chunk}, instruction {path.Instruction}).");
            return;
        }

        fireAndForgetNodes.Add(path.CurrentNode);

        if (!forkingInstructions.Add((path.Chunk, path.Instruction)))
        {
            return;
        }

        // The node itself is walked in the child cursor, the flow leaving it is the parent's
        var child = path.Fork(destChunk, destInstruction, path.DefaultOutflow);
        child.LastSequencePoint = null;
        pathQueue.Enqueue(child);
    }

    private void AddSetVariable(FlowPath path, KVObject instruction, bool notifiesListeners)
    {
        var nodeId = path.CurrentNode >= 0 ? path.CurrentNode : AllocateNodeId();
        if (nodes.ContainsKey(nodeId))
        {
            return;
        }

        var variableIndex = instruction.GetInt32Property("m_nVar");
        var node = AddNode(nodeId, "Step_SetVar", isFlow: true);
        node.Fields.Add("m_VarDefID", variableNodeIds[variableIndex]);
        SetInputPort(node, node.Fields, "m_Value", "m_Value", GetRegister(path, instruction.GetInt32Property("m_nReg0")));

        // Writes to an observable variable notify its listeners unless the node opted out
        if (!notifiesListeners && variables[variableIndex].GetBooleanProperty("m_bIsObservable"))
        {
            node.Fields.Add("m_bDoNotNotifyListeners", true);
        }
    }

    private KVObject? GetTempVar(int chunk, KVObject instruction)
    {
        var bank = chunks[chunk].GetInt32Property("m_nTempVarBank", -1);
        var tempVars = graph.GetArray("m_TempVarBanks")?.ElementAtOrDefault(bank)?.GetArray("m_TempVars");
        var tempVar = tempVars?.ElementAtOrDefault(instruction.GetInt32Property("m_nTempVarIdx", -1));

        if (tempVar == null)
        {
            Report($"Could not find temporary variable {instruction.GetInt32Property("m_nTempVarIdx", -1)} of chunk {chunk}.");
        }

        return tempVar;
    }

    // The node that declares a temporary variable writes its initial value, any other write is a Set Variable
    private void AddSetTempVar(FlowPath path, KVObject instruction)
    {
        if (nodes.ContainsKey(path.CurrentNode))
        {
            return;
        }

        var tempVar = GetTempVar(path.Chunk, instruction);
        var declaringNode = tempVar?.GetInt32Property("m_nEditorNodeID", -1) ?? -1;
        var value = GetRegister(path, instruction.GetInt32Property("m_nReg0"));

        if (declaringNode == path.CurrentNode || declaringNode < 0)
        {
            var node = AddNode(path.CurrentNode, "Step_CreateTempVar", isFlow: true);
            node.Fields.Add("m_VarName", tempVar?.GetStringProperty("m_Name", string.Empty) ?? string.Empty);
            node.Fields.Add("m_ValueType", tempVar?.GetStringProperty("m_Type", "PVAL_INVALID") ?? "PVAL_INVALID");
            SetInputPort(node, node.Fields, "m_Value", "m_Value", value);
        }
        else
        {
            var node = AddNode(path.CurrentNode, "Step_SetVar", isFlow: true);
            node.Fields.Add("m_VarDefID", declaringNode);
            SetInputPort(node, node.Fields, "m_Value", "m_Value", value);
        }
    }

    /// <returns>Whether the flow continues past this instruction.</returns>
    private bool AddBranch(FlowPath path, KVObject instruction, int valueNode, string? sequencePoint, bool isChunkLeap)
    {
        if (!forkingInstructions.Add((path.Chunk, path.Instruction)))
        {
            return false;
        }

        var conditionRegister = instruction.GetInt32Property("m_nReg0");
        var condition = GetRegister(path, conditionRegister);
        var takenPath = isChunkLeap
            ? path.Fork(instruction.GetInt32Property("m_nChunk"), 0, null)
            : path.Fork(path.Chunk, instruction.GetInt32Property("m_nDestInstruction"), null);

        if (!isChunkLeap && valueNode >= 0 && valueNode != path.CurrentNode)
        {
            return AddTernary(path, takenPath, condition, valueNode);
        }

        if (sequencePoint == "m_LoopHeader")
        {
            AddLoop(path, condition);
            takenPath.DefaultOutflow = "m_Outflow_Loop";
            path.DefaultOutflow = "m_Outflow_Completed";
        }
        else if (condition is CaseCondition caseCondition)
        {
            var caseName = AddSwitchCase(path, caseCondition);
            takenPath.DefaultOutflow = caseName;
            path.DefaultOutflow = "m_DefaultCaseOutflow";
        }
        else
        {
            AddIf(path, condition);
            takenPath.DefaultOutflow = "m_IsTrue";
            path.DefaultOutflow = "m_IsFalse";
        }

        pathQueue.Enqueue(takenPath);
        path.Instruction++;
        return true;
    }

    private void AddIf(FlowPath path, ValueRef condition)
    {
        if (nodes.TryGetValue(path.CurrentNode, out var existing))
        {
            if (existing.ClassName != "Outflow_If")
            {
                Report($"Node {existing.Id} ({existing.ClassName}) branches in a way that is not supported yet.");
            }

            return;
        }

        var node = AddNode(path.CurrentNode, "Outflow_If", isFlow: true);

        if (condition is InvertedValue inverted)
        {
            node.Fields.Add("m_bNotCondition", true);
            condition = inverted.Inner;
        }

        SetInputPort(node, node.Fields, "m_Condition", "m_bCondition", condition);
    }

    private void AddLoop(FlowPath path, ValueRef condition)
    {
        var nodeId = path.CurrentNode;
        if (nodes.ContainsKey(nodeId))
        {
            return;
        }

        if (condition is InternalValue { Port: "__loop_cond" })
        {
            AddRangeLoop(path, nodeId);
            return;
        }

        var node = AddNode(nodeId, "Outflow_LoopWhile", isFlow: true);
        node.InflowName = "m_Inflow_Start";

        if (condition is InvertedValue inverted)
        {
            node.Fields.Add("m_bNotCondition", true);
            condition = inverted.Inner;
        }

        SetInputPort(node, node.Fields, "m_Condition", "m_Condition", condition);
    }

    // A range loop keeps its index in the register its start value was loaded into, and tests it against the stop value
    private void AddRangeLoop(FlowPath path, int nodeId)
    {
        var indexRegister = FindRegisterByOrigin(path.Chunk, nodeId, "m_Start");
        var stepRegister = FindRegisterByOrigin(path.Chunk, nodeId, "m_Step");
        var includesEndpoint = FindRegisterByOrigin(path.Chunk, nodeId, "index <= stop") >= 0;
        var stopTestRegister = FindRegisterByOrigin(path.Chunk, nodeId, includesEndpoint ? "index <= stop" : "index < stop");

        var stop = stopTestRegister >= 0 && path.Registers.GetValueOrDefault(stopTestRegister) is InternalValue { Second: { } stopValue }
            ? stopValue
            : new UnknownValue();

        var isFloat = indexRegister >= 0 && GetRegisterType(path.Chunk, indexRegister) == "PVAL_FLOAT";
        if (isFloat)
        {
            Report($"Float range loop {nodeId} is not supported yet, it is decompiled as an integer range.");
        }

        var node = AddNode(nodeId, "Outflow_LoopIntegerRange", isFlow: true);
        node.InflowName = "m_Inflow_Start";
        SetInputPort(node, node.Fields, "m_Start", "m_Start", indexRegister >= 0 ? GetRegister(path, indexRegister) : new UnknownValue());
        SetInputPort(node, node.Fields, "m_Stop", "m_Stop", stop);
        SetInputPort(node, node.Fields, "m_Step", "m_Step", stepRegister >= 0 ? GetRegister(path, stepRegister) : new UnknownValue());

        if (includesEndpoint)
        {
            node.Fields.Add("m_bRangeIncludesEndpoint", true);
        }

        if (indexRegister >= 0)
        {
            loopIndexRegisters[nodeId] = indexRegister;
        }
    }

    // Switches test each case in turn, the outflow of a case is named after its value
    private string AddSwitchCase(FlowPath path, CaseCondition condition)
    {
        if (!nodes.TryGetValue(path.CurrentNode, out var node))
        {
            var (className, portName) = condition.SwitchType switch
            {
                "PVAL_STRING" => ("Outflow_StringSwitch", "pSwitchValue"),
                var type when type.StartsWith("PVAL_SCHEMA_ENUM", StringComparison.Ordinal) || type.StartsWith("PVAL_ENUM", StringComparison.Ordinal)
                    => ("Outflow_SchemaEnumSwitch", "nSwitchValue"),
                _ => ("Outflow_IntSwitch", "nSwitchValue"),
            };

            node = AddNode(path.CurrentNode, className, isFlow: true);
            SetInputPort(node, node.Fields, "m_SwitchValue", portName, condition.SwitchValue);
            node.Fields.Add("m_Cases", KVObject.Array());
        }

        if (condition.CaseValue is not LiteralValue caseValue)
        {
            Report($"Switch {node.Id} compares against a case value that is not a constant.");
            return "m_DefaultCaseOutflow";
        }

        node.Fields["m_Cases"].Add(caseValue.Value);
        return caseValue.Value.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// A ternary compiles to a branch inside a value: each side copies its value into the result register, then both
    /// meet again. Both sides are evaluated here and the flow continues where they meet.
    /// </summary>
    /// <returns>Whether the flow continues past this instruction.</returns>
    private bool AddTernary(FlowPath path, FlowPath truePath, ValueRef condition, int nodeId)
    {
        var falsePath = path.Fork(path.Chunk, path.Instruction + 1, null);

        if (!TryEvaluateTernaryBranch(falsePath, out var falseValue, out var resultRegister, out var mergeInstruction)
            || !TryEvaluateTernaryBranch(truePath, out var trueValue, out _, out _))
        {
            Report($"Could not follow the branches of ternary {nodeId}.");
            return false;
        }

        if (!nodes.ContainsKey(nodeId))
        {
            var node = AddNode(nodeId, "ExprTernaryConditional", isFlow: false);
            SetInputPort(node, node.Fields, "m_Condition", "cond", condition);
            SetInputPort(node, node.Fields, "m_TrueVal", "true_val", trueValue, refineLiteralType: true);
            SetInputPort(node, node.Fields, "m_FalseVal", "false_val", falseValue, refineLiteralType: true);
        }

        path.Registers[resultRegister] = new NodeOutput(nodeId, "retval");
        path.Instruction = mergeInstruction;
        return true;
    }

    private bool TryEvaluateTernaryBranch(FlowPath branch, out ValueRef value, out int resultRegister, out int mergeInstruction)
    {
        var instructions = chunks[branch.Chunk].GetArray("m_Instructions");

        for (var steps = 0; steps < instructions.Count && branch.Instruction >= 0 && branch.Instruction < instructions.Count; steps++)
        {
            var instruction = instructions[branch.Instruction];
            var code = instruction.GetStringProperty("m_nCode", string.Empty);

            if (code == "COPY")
            {
                value = GetRegister(branch, instruction.GetInt32Property("m_nReg1"));
                resultRegister = instruction.GetInt32Property("m_nReg0");
                mergeInstruction = branch.Instruction + 1;

                if (mergeInstruction < instructions.Count && instructions[mergeInstruction].GetStringProperty("m_nCode", string.Empty) == "JUMP")
                {
                    mergeInstruction = instructions[mergeInstruction].GetInt32Property("m_nDestInstruction");
                }

                return true;
            }

            var (_, valueNode, sequencePoint) = GetDebugInfo(branch.Chunk, branch.Instruction);
            if (!ExecuteInstruction(branch, instruction, valueNode, sequencePoint))
            {
                break;
            }
        }

        value = new UnknownValue();
        resultRegister = -1;
        mergeInstruction = -1;
        return false;
    }
}
