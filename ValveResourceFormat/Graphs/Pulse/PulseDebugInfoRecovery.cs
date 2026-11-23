using System.Globalization;
using System.Linq;
using ValveResourceFormat.Serialization.KeyValues;

namespace ValveResourceFormat.Graphs;

/// <summary>
/// Guesses the debug info of chunks compiled without it, so <see cref="PulseGraphWalker"/> can rebuild their editor nodes.
/// </summary>
/// <remarks>
/// Instructions are grouped into editor nodes by the shapes the compiler emits: a loop by its back edge, a switch by its
/// run of case tests and a ternary by both branches copying into one register. Any other node is the instruction that
/// runs it, together with the value instructions computing its inputs right before it. Node ids the graph keeps outside
/// the debug info, such as those of cells, call infos and register origins, are used where they survive, every other
/// node gets a fresh id. Registers without an origin get the one the compiler names the bookkeeping of a node by.
/// </remarks>
internal sealed class PulseDebugInfoRecovery
{
    private readonly PulseGraphDefinition definition;
    private readonly int[] cellNodeIds;
    private readonly Dictionary<int, ChunkRecovery> recoveries = [];
    private int nextNodeId;

    private PulseDebugInfoRecovery(PulseGraphDefinition definition, int[] cellNodeIds)
    {
        this.definition = definition;
        this.cellNodeIds = cellNodeIds;
        nextNodeId = definition.FirstUnusedNodeId();
    }

    /// <summary>Recovers every chunk without debug info, giving cells without an editor node id a fresh one.</summary>
    /// <param name="definition">The graph, whose chunks receive the recovered debug info.</param>
    /// <param name="cellNodeIds">Editor node id of each cell, -1 where the graph does not keep it.</param>
    public static void Recover(PulseGraphDefinition definition, int[] cellNodeIds)
    {
        if (definition.Chunks.All(static chunk => chunk.HasDebugInfo))
        {
            return;
        }

        var recovery = new PulseDebugInfoRecovery(definition, cellNodeIds);

        for (var i = 0; i < cellNodeIds.Length; i++)
        {
            if (cellNodeIds[i] < 0)
            {
                cellNodeIds[i] = recovery.AllocateNodeId();
            }
        }

        for (var i = 0; i < definition.Chunks.Count; i++)
        {
            if (!definition.Chunks[i].HasDebugInfo)
            {
                definition.Report($"Chunk {i} has no debug info, its editor nodes are guessed from the shape of its instructions.");

                var chunk = new ChunkRecovery(recovery, i);
                chunk.Recover();
                recovery.recoveries[i] = chunk;
            }
        }

        foreach (var chunk in recovery.recoveries.Values)
        {
            chunk.ResolveChildCursors();
        }

        foreach (var chunk in recovery.recoveries.Values)
        {
            chunk.Apply();
        }
    }

    private int AllocateNodeId() => nextNodeId++;

    // The flow node an instruction runs under, recovered or kept by the graph
    private int FlowNodeAt(int chunk, int instruction)
    {
        if (recoveries.TryGetValue(chunk, out var recovered))
        {
            return recovered.FlowNodeAt(instruction);
        }

        var debugInfos = definition.Chunks[chunk].DebugInfos;
        return instruction >= 0 && instruction < debugInfos.Count ? debugInfos[instruction].FlowNode : -1;
    }

    private static bool ComputesValue(PulseInstructionCode code) => code
        is PulseInstructionCode.GET_CONST or PulseInstructionCode.GET_DOMAIN_VALUE or PulseInstructionCode.GET_VAR
        or PulseInstructionCode.GET_VAR_DETACH or PulseInstructionCode.GET_TEMPVAR or PulseInstructionCode.GET_BLACKBOARD_REFERENCE
        or PulseInstructionCode.GET_ARRAY_ELEMENT or PulseInstructionCode.ELEMENT_ACCESS or PulseInstructionCode.COPY
        or PulseInstructionCode.CONVERT_VALUE or PulseInstructionCode.REINTERPRET_INSTANCE
        or >= PulseInstructionCode.NOT and <= PulseInstructionCode.SCALE_INV;

    private static bool IsLessThan(PulseInstructionCode code) => code is PulseInstructionCode.LT or PulseInstructionCode.LTE;

    private sealed class ChunkRecovery
    {
        // Flow node not decided yet, which becomes -1 to stay on the node the cursor is in
        private const int Unassigned = int.MinValue;

        private const string ArrayAppend = "CPulseArraylib::ArrayAppend";
        private const string ArrayCount = "CPulseArraylib::ArrayCount";

        private readonly PulseDebugInfoRecovery owner;
        private readonly PulseGraphDefinition definition;
        private readonly int chunkIndex;
        private readonly PulseChunk chunk;
        private readonly IReadOnlyList<PulseInstruction> instructions;

        private readonly int[] flowNodes;
        private readonly int[] valueNodes;
        private readonly string[] sequencePoints;

        // Value instructions take the flow node of the instruction after them, or after the ternary they are part of
        private readonly bool[] inheritsFlowNode;
        private readonly int[] inheritFrom;

        private readonly Dictionary<int, PulseRegister> registers;
        private readonly Dictionary<int, int> readCounts = [];

        private readonly HashSet<int> claimedBranches = [];
        private readonly List<(int Node, int Start, int End)> loopRegions = [];

        // Array count invokes and element reads a for-each loop runs itself, rather than separate value nodes
        private readonly HashSet<int> forEachInstructions = [];
        private readonly HashSet<int> arrayAppends = [];
        private readonly Dictionary<(string FuncName, int Register), int> valueInvokeNodes = [];
        private readonly HashSet<int> declaredTempVars = [];
        private readonly List<int> childCursors = [];

        public ChunkRecovery(PulseDebugInfoRecovery owner, int chunkIndex)
        {
            this.owner = owner;
            this.chunkIndex = chunkIndex;
            definition = owner.definition;
            chunk = definition.Chunks[chunkIndex];
            instructions = chunk.Instructions;

            var count = instructions.Count;
            flowNodes = [.. Enumerable.Repeat(Unassigned, count)];
            valueNodes = [.. Enumerable.Repeat(-1, count)];
            sequencePoints = [.. Enumerable.Repeat(string.Empty, count)];
            inheritsFlowNode = new bool[count];
            inheritFrom = [.. Enumerable.Range(1, count)];

            registers = chunk.Registers.ToDictionary(static register => register.Index);

            for (var i = 0; i < count; i++)
            {
                foreach (var register in Reads(instructions[i]))
                {
                    readCounts[register] = readCounts.GetValueOrDefault(register) + 1;
                }

                foreach (var register in Writes(instructions[i]))
                {
                    if (registers.TryGetValue(register, out var info) && info.WrittenByInstruction < 0)
                    {
                        registers[register] = info with { WrittenByInstruction = i };
                    }
                }
            }
        }

        public int FlowNodeAt(int instruction) => instruction >= 0 && instruction < flowNodes.Length ? flowNodes[instruction] : -1;

        public void Recover()
        {
            FindLoops();
            FindSwitches();
            FindTernaries();

            for (var i = 0; i < instructions.Count; i++)
            {
                AssignNode(i);
            }

            foreach (var (node, start, end) in loopRegions)
            {
                for (var i = start; i <= end; i++)
                {
                    if (flowNodes[i] == Unassigned)
                    {
                        flowNodes[i] = node;
                    }
                }
            }

            for (var i = instructions.Count - 1; i >= 0; i--)
            {
                if (flowNodes[i] == Unassigned)
                {
                    var next = inheritFrom[i];
                    flowNodes[i] = inheritsFlowNode[i] && next < flowNodes.Length ? flowNodes[next] : -1;
                }
            }
        }

        // A child cursor runs under the node it starts, which is the one at its destination
        public void ResolveChildCursors()
        {
            foreach (var index in childCursors)
            {
                var instruction = instructions[index];
                flowNodes[index] = owner.FlowNodeAt(instruction.Chunk, instruction.DestInstruction);
            }
        }

        public void Apply()
        {
            var debugInfos = new PulseInstructionDebug[instructions.Count];

            for (var i = 0; i < debugInfos.Length; i++)
            {
                debugInfos[i] = new PulseInstructionDebug(flowNodes[i], valueNodes[i], sequencePoints[i]);
            }

            chunk.SetRecoveredDebugInfo(debugInfos, registers.Values);
        }

        private int AllocateNodeId() => owner.AllocateNodeId();

        private void Report(string message) => definition.Report(message);

        private PulseInvokeBinding Binding(PulseInstruction instruction) => definition.InvokeBindings[instruction.InvokeBindingIndex];

        private PulseCallInfo? CallInfo(PulseInstruction instruction)
            => instruction.CallInfoIndex >= 0 && instruction.CallInfoIndex < definition.CallInfos.Count ? definition.CallInfos[instruction.CallInfoIndex] : null;

        private IEnumerable<int> Writes(PulseInstruction instruction)
        {
            IEnumerable<int> written = instruction.Code switch
            {
                PulseInstructionCode.LIBRARY_INVOKE or PulseInstructionCode.CELL_INVOKE => Binding(instruction).RegisterMap.OutParams.Select(static param => param.Register),
                PulseInstructionCode.BROADCAST_INVOKE => [instruction.Reg1, .. Binding(instruction).RegisterMap.OutParams.Select(static param => param.Register)],
                PulseInstructionCode.PULSE_CALL_SYNC => CallInfo(instruction)?.RegisterMap.OutParams.Select(static param => param.Register) ?? [],
                var code when ComputesValue(code) => [instruction.Reg0],
                _ => [],
            };

            return written.Where(static register => register >= 0);
        }

        private IEnumerable<int> Reads(PulseInstruction instruction)
        {
            IEnumerable<int> read = instruction.Code switch
            {
                PulseInstructionCode.LIBRARY_INVOKE or PulseInstructionCode.CELL_INVOKE => Binding(instruction).RegisterMap.InParams.Select(static param => param.Register),
                PulseInstructionCode.BROADCAST_INVOKE => [instruction.Reg0, .. Binding(instruction).RegisterMap.InParams.Select(static param => param.Register)],
                PulseInstructionCode.PULSE_CALL_SYNC or PulseInstructionCode.PULSE_CALL_ASYNC_FIRE or PulseInstructionCode.CREATE_CHILD_CURSOR_OUTFLOW
                    => CallInfo(instruction)?.RegisterMap.InParams.Select(static param => param.Register) ?? [],
                PulseInstructionCode.SET_VAR_ARRAY_ELEMENT_1D => [instruction.Reg0, instruction.Reg2],
                PulseInstructionCode.SET_VAR or PulseInstructionCode.SET_VAR_OBSERVABLE or PulseInstructionCode.SET_TEMPVAR
                    or PulseInstructionCode.SET_TEMPVAR_OBSERVABLE or PulseInstructionCode.SET_BLACKBOARD_REFERENCE
                    or PulseInstructionCode.JUMP_COND or PulseInstructionCode.CHUNK_LEAP_COND or PulseInstructionCode.RETURN_VALUE => [instruction.Reg0],
                var code when ComputesValue(code) => [instruction.Reg1, instruction.Reg2],
                _ => [],
            };

            return read.Where(static register => register >= 0);
        }

        // The last instruction before the given one that writes the register, or -1
        private int WriterBefore(int register, int index)
        {
            for (var i = index - 1; i >= 0; i--)
            {
                if (Writes(instructions[i]).Contains(register))
                {
                    return i;
                }
            }

            return -1;
        }

        private bool IsRead(int register) => readCounts.GetValueOrDefault(register) > 0;

        private int OriginNode(int register) => registers.TryGetValue(register, out var info) ? info.OriginNode : -1;

        private string? OriginPort(int register) => registers.TryGetValue(register, out var info) && info.OriginNode >= 0 ? info.OriginPort : null;

        // Names a register the way the compiler names a node's bookkeeping, unless it kept its own origin
        private void SetOrigin(int register, int node, string port)
        {
            if (register < 0)
            {
                return;
            }

            if (!registers.TryGetValue(register, out var info))
            {
                var writer = Enumerable.Range(0, instructions.Count).FirstOrDefault(i => Writes(instructions[i]).Contains(register), -1);
                info = new PulseRegister(register, "PVAL_INVALID", PulseValueType.PVAL_INVALID, -1, string.Empty, writer);
            }

            if (info.OriginNode < 0)
            {
                registers[register] = info with { OriginNode = node, OriginPort = port };
            }
        }

        // A literal loaded for one port keeps the id of the node owning that port in its origin
        private int LiteralConsumer(int index, IEnumerable<int> inputs)
        {
            foreach (var register in inputs)
            {
                var writer = WriterBefore(register, index);

                if (writer >= 0 && instructions[writer].Code is PulseInstructionCode.GET_CONST or PulseInstructionCode.GET_DOMAIN_VALUE && OriginNode(register) >= 0)
                {
                    return OriginNode(register);
                }
            }

            return -1;
        }

        private int KnownOrFreshNodeId(int id) => id >= 0 ? id : AllocateNodeId();

        // A loop ends with a jump back to its condition followed by a jump to its exit, the exit its condition jumps to as well
        private void FindLoops()
        {
            for (var backJump = 0; backJump + 1 < instructions.Count; backJump++)
            {
                var instruction = instructions[backJump];
                var exitJump = instructions[backJump + 1];

                if (instruction.Code != PulseInstructionCode.JUMP || instruction.DestInstruction >= backJump || exitJump.Code != PulseInstructionCode.JUMP)
                {
                    continue;
                }

                var header = Math.Max(instruction.DestInstruction, 0);
                var branch = -1;

                for (var i = header; i + 1 < backJump; i++)
                {
                    if (instructions[i].Code == PulseInstructionCode.JUMP_COND
                        && instructions[i + 1].Code == PulseInstructionCode.JUMP && instructions[i + 1].DestInstruction == exitJump.DestInstruction)
                    {
                        branch = i;
                        break;
                    }
                }

                if (branch < 0)
                {
                    Report($"Instruction {backJump} of chunk {chunkIndex} jumps back in a way no known loop compiles to.");
                    continue;
                }

                AddLoop(header, branch, backJump, Math.Clamp(exitJump.DestInstruction, backJump + 1, instructions.Count - 1));
            }
        }

        private void AddLoop(int header, int branch, int backJump, int exit)
        {
            var bodyCall = -1;
            var callOwner = -1;

            for (var i = branch + 1; i < backJump; i++)
            {
                if (instructions[i].Code == PulseInstructionCode.PULSE_CALL_SYNC && CallInfo(instructions[i]) is { CallMethodId: < 0 } callInfo)
                {
                    bodyCall = i;
                    callOwner = callInfo.EditorNode;
                    break;
                }
            }

            var condition = instructions[branch].Reg0;
            var node = KnownOrFreshNodeId(callOwner >= 0 ? callOwner : OriginNode(condition));

            claimedBranches.Add(branch);
            sequencePoints[branch] = "m_LoopHeader";
            loopRegions.Add((node, header, exit));

            var test = WriterBefore(condition, branch);
            if (test < 0)
            {
                return;
            }

            var testInstruction = instructions[test];

            if (testInstruction.Code == PulseInstructionCode.OR && TryAddIntegerRange(node, header, test))
            {
                return;
            }

            if (IsLessThan(testInstruction.Code) && test == header
                && (TryAddForEach(node, test, branch, backJump) || TryAddFloatRange(node, test, branch, bodyCall)))
            {
                return;
            }

            if (testInstruction.Code == PulseInstructionCode.NOT && readCounts.GetValueOrDefault(condition) == 1)
            {
                SetOrigin(condition, node, "not_condition");
            }

            // A counter kept in a register cannot be written in a while loop, only a variable can
            for (var i = branch + 1; i < backJump; i++)
            {
                if (instructions[i].Code is PulseInstructionCode.ADD or PulseInstructionCode.SUB
                    && (instructions[i].Reg0 == testInstruction.Reg1 || instructions[i].Reg0 == testInstruction.Reg2))
                {
                    Report($"Loop {node} in chunk {chunkIndex} counts in a register in a way no known loop compiles to, it is kept as a while loop.");
                    break;
                }
            }
        }

        // Tests index < stop and stop > index, each and-ed with the sign of the step, then or-ed together
        private bool TryAddIntegerRange(int node, int header, int test)
        {
            if (test != header + 4)
            {
                return false;
            }

            var (forward, backward, positive, negative, either) = (instructions[header], instructions[header + 1], instructions[header + 2], instructions[header + 3], instructions[test]);

            if (!IsLessThan(forward.Code) || !IsLessThan(backward.Code) || backward.Reg1 != forward.Reg2 || backward.Reg2 != forward.Reg1
                || positive.Code != PulseInstructionCode.AND || positive.Reg2 != forward.Reg0
                || negative.Code != PulseInstructionCode.AND || negative.Reg2 != backward.Reg0
                || either.Reg1 != positive.Reg0 || either.Reg2 != negative.Reg0)
            {
                return false;
            }

            var stepPositive = WriterBefore(positive.Reg1, header);
            var stepNegative = WriterBefore(negative.Reg1, header);

            if (stepPositive < 0 || stepNegative < 0 || instructions[stepPositive].Code != PulseInstructionCode.LT || instructions[stepNegative].Code != PulseInstructionCode.LT)
            {
                return false;
            }

            SetOrigin(forward.Reg0, node, "index < stop");
            SetOrigin(backward.Reg0, node, "stop > index");
            SetOrigin(positive.Reg0, node, "step > 0 && index < stop");
            SetOrigin(negative.Reg0, node, "step < 0 && index > stop");
            SetOrigin(either.Reg0, node, "__loop_cond");
            SetOrigin(instructions[stepPositive].Reg0, node, "step > 0");
            SetOrigin(instructions[stepNegative].Reg0, node, "step < 0");
            SetOrigin(instructions[stepPositive].Reg1, node, "__zero");

            // The index counts in the register its start value was loaded into, unless an expression node computed it
            var startWriter = WriterBefore(forward.Reg1, header);

            if (startWriter < 0 || instructions[startWriter].Code is not (>= PulseInstructionCode.NOT and <= PulseInstructionCode.SCALE_INV))
            {
                SetOrigin(forward.Reg1, node, "m_Start");
            }

            return true;
        }

        // Tests the index against the element count, then reads the element of each iteration
        private bool TryAddForEach(int node, int test, int branch, int backJump)
        {
            var (index, count) = (instructions[test].Reg1, instructions[test].Reg2);
            var counter = WriterBefore(count, test);

            if (counter < 0 || instructions[counter].Code != PulseInstructionCode.LIBRARY_INVOKE || Binding(instructions[counter]).FuncName != ArrayCount)
            {
                return false;
            }

            forEachInstructions.Add(counter);
            SetOrigin(instructions[test].Reg0, node, "__loop_cond");
            SetOrigin(index, node, "__loop_index");

            for (var i = branch + 1; i < backJump; i++)
            {
                var instruction = instructions[i];

                if (instruction.Code == PulseInstructionCode.GET_ARRAY_ELEMENT && instruction.Reg2 == index)
                {
                    forEachInstructions.Add(i);
                }
                else if (instruction.Code == PulseInstructionCode.ADD && instruction.Reg0 == index)
                {
                    SetOrigin(instruction.Reg2, node, "__increment");
                }
            }

            return true;
        }

        // Divides the span between both ends by the steps, then derives the value of each iteration from the index
        private bool TryAddFloatRange(int node, int test, int branch, int bodyCall)
        {
            if (test < 4 || bodyCall < 0)
            {
                return false;
            }

            var (index, count) = (instructions[test].Reg1, instructions[test].Reg2);
            var (span, countLessOne, convert, step) = (instructions[test - 4], instructions[test - 3], instructions[test - 2], instructions[test - 1]);

            if (step.Code != PulseInstructionCode.DIV || convert.Code != PulseInstructionCode.CONVERT_VALUE || convert.Reg0 != step.Reg2
                || countLessOne.Code != PulseInstructionCode.SUB || countLessOne.Reg0 != convert.Reg1 || countLessOne.Reg1 != count
                || span.Code != PulseInstructionCode.SUB || span.Reg0 != step.Reg1)
            {
                return false;
            }

            SetOrigin(instructions[test].Reg0, node, "index < stop");
            SetOrigin(span.Reg0, node, "end - start");
            SetOrigin(countLessOne.Reg0, node, "count - 1");
            SetOrigin(step.Reg0, node, "(end - start) / (count - 1)");
            SetOrigin(index, node, "__index");
            SetOrigin(countLessOne.Reg2, node, "__increment");

            var offset = -1;

            for (var i = branch + 1; i < bodyCall; i++)
            {
                var instruction = instructions[i];

                if (instruction.Code == PulseInstructionCode.MUL && instruction.Reg2 == step.Reg0)
                {
                    offset = instruction.Reg0;
                    SetOrigin(offset, node, "index * delta");
                }
                else if (instruction.Code == PulseInstructionCode.ADD && instruction.Reg2 == offset)
                {
                    SetOrigin(instruction.Reg0, node, "start + (index * delta)");
                }
            }

            return true;
        }

        // A switch tests the same value against each case constant in turn, then jumps to its default case
        private void FindSwitches()
        {
            for (var start = 0; start < instructions.Count; start++)
            {
                if (instructions[start].Code != PulseInstructionCode.JUMP_COND || claimedBranches.Contains(start))
                {
                    continue;
                }

                var cases = new List<(int Test, int Branch)>();
                var switchValue = -1;
                var branch = start;

                while (TryGetCaseTest(branch, ref switchValue, out var test))
                {
                    cases.Add((test, branch));

                    var next = branch + 1;
                    while (next < instructions.Count && ComputesValue(instructions[next].Code))
                    {
                        next++;
                    }

                    branch = next;
                }

                var defaultJump = cases.Count > 0 ? cases[^1].Branch + 1 : -1;

                if (cases.Count < 2 || defaultJump >= instructions.Count || instructions[defaultJump].Code != PulseInstructionCode.JUMP)
                {
                    continue;
                }

                var node = SwitchNode(cases, defaultJump);

                foreach (var (test, caseBranch) in cases)
                {
                    claimedBranches.Add(caseBranch);
                    flowNodes[test] = node;
                    flowNodes[caseBranch] = node;
                    SetOrigin(instructions[test].Reg0, node, "__case_cond");
                }

                flowNodes[defaultJump] = node;

                if (defaultJump + 1 < instructions.Count && instructions[defaultJump + 1].Code == PulseInstructionCode.IMMEDIATE_HALT)
                {
                    flowNodes[defaultJump + 1] = node;
                }

                start = defaultJump;
            }
        }

        private int SwitchNode(List<(int Test, int Branch)> cases, int defaultJump)
        {
            var condition = instructions[cases[0].Test].Reg0;

            if (OriginPort(condition) == "__case_cond")
            {
                return OriginNode(condition);
            }

            // A case calling a shared flow names the outflow by the case value
            var node = OutflowCallOwner(instructions[defaultJump].DestInstruction, "m_DefaultCaseOutflow");

            foreach (var (test, branch) in cases)
            {
                if (node >= 0)
                {
                    break;
                }

                var caseConstant = instructions[WriterBefore(instructions[test].Reg2, test)];
                var caseName = definition.Constants[caseConstant.ConstIdx]["m_Value"].ToString(CultureInfo.InvariantCulture);
                node = OutflowCallOwner(instructions[branch].DestInstruction, caseName);
            }

            return KnownOrFreshNodeId(node);
        }

        private bool TryGetCaseTest(int branch, ref int switchValue, out int test)
        {
            test = -1;

            if (branch >= instructions.Count || instructions[branch].Code != PulseInstructionCode.JUMP_COND || claimedBranches.Contains(branch))
            {
                return false;
            }

            var condition = instructions[branch].Reg0;
            test = WriterBefore(condition, branch);

            if (test < 0 || instructions[test].Code != PulseInstructionCode.EQ || OriginPort(condition) is not (null or "__case_cond"))
            {
                return false;
            }

            var caseConstant = WriterBefore(instructions[test].Reg2, test);

            if (caseConstant < 0 || instructions[caseConstant].Code != PulseInstructionCode.GET_CONST || (switchValue >= 0 && instructions[test].Reg1 != switchValue))
            {
                return false;
            }

            switchValue = instructions[test].Reg1;
            return true;
        }

        // A ternary branches inside a value: each side copies its value into the result register, then both meet again
        private void FindTernaries()
        {
            for (var branch = 0; branch < instructions.Count; branch++)
            {
                if (instructions[branch].Code != PulseInstructionCode.JUMP_COND || claimedBranches.Contains(branch)
                    || !TryFindCopy(branch + 1, instructions.Count, out var falseCopy)
                    || falseCopy + 1 >= instructions.Count || instructions[falseCopy + 1].Code != PulseInstructionCode.JUMP)
                {
                    continue;
                }

                var merge = instructions[falseCopy + 1].DestInstruction;
                var result = instructions[falseCopy].Reg0;

                if (merge <= falseCopy + 1 || merge > instructions.Count
                    || !TryFindCopy(instructions[branch].DestInstruction, merge, out var trueCopy) || trueCopy + 1 != merge || instructions[trueCopy].Reg0 != result)
                {
                    continue;
                }

                claimedBranches.Add(branch);
                valueNodes[branch] = KnownOrFreshNodeId(OriginNode(result));

                for (var i = branch; i < merge; i++)
                {
                    inheritsFlowNode[i] = true;
                    inheritFrom[i] = merge;
                }
            }
        }

        private bool TryFindCopy(int start, int end, out int copy)
        {
            for (copy = start; copy >= 0 && copy < end; copy++)
            {
                var code = instructions[copy].Code;

                if (code == PulseInstructionCode.COPY)
                {
                    return true;
                }

                if (!ComputesValue(code) && code is not (PulseInstructionCode.LIBRARY_INVOKE or PulseInstructionCode.CELL_INVOKE))
                {
                    break;
                }
            }

            return false;
        }

        private void AssignNode(int index)
        {
            var instruction = instructions[index];

            switch (instruction.Code)
            {
                case PulseInstructionCode.NOP:
                    inheritsFlowNode[index] = true;
                    break;

                case PulseInstructionCode.GET_CONST:
                    inheritsFlowNode[index] = true;
                    AddEmptyArray(index);
                    break;

                case PulseInstructionCode.GET_ARRAY_ELEMENT:
                case PulseInstructionCode.ELEMENT_ACCESS:
                    inheritsFlowNode[index] = true;

                    if (!forEachInstructions.Contains(index))
                    {
                        valueNodes[index] = KnownOrFreshNodeId(OriginNode(instruction.Reg0));
                    }

                    if (instruction.Code == PulseInstructionCode.ELEMENT_ACCESS)
                    {
                        NamePropertyRead(index);
                    }

                    break;

                case var code when ComputesValue(code):
                    inheritsFlowNode[index] = true;
                    break;

                case PulseInstructionCode.LIBRARY_INVOKE:
                    AssignLibraryInvoke(index);
                    break;

                case PulseInstructionCode.CELL_INVOKE:
                case PulseInstructionCode.BROADCAST_INVOKE:
                {
                    var cellIndex = Binding(instruction).CellIndex;
                    var node = owner.cellNodeIds[cellIndex];

                    if (instruction.Code == PulseInstructionCode.CELL_INVOKE && PulseHues.CategoryOf(definition.CellClass(cellIndex)) == PulseCategory.ValueCell)
                    {
                        inheritsFlowNode[index] = true;
                        valueNodes[index] = node;
                    }
                    else
                    {
                        flowNodes[index] = node;
                    }

                    break;
                }

                case PulseInstructionCode.PULSE_CALL_SYNC:
                case PulseInstructionCode.PULSE_CALL_ASYNC_FIRE:
                {
                    // A call into an outflow of a node stays on the node making it when the graph does not name it
                    var callInfo = CallInfo(instruction);
                    var callOwner = callInfo?.EditorNode ?? -1;

                    if (callInfo is { CallMethodId: < 0 } && callOwner < 0)
                    {
                        break;
                    }

                    flowNodes[index] = KnownOrFreshNodeId(callOwner);
                    break;
                }

                case PulseInstructionCode.CREATE_CHILD_CURSOR_OUTFLOW:
                {
                    var callInfo = CallInfo(instruction);
                    var portName = callInfo?.PortName ?? string.Empty;
                    var separator = portName.IndexOf(':', StringComparison.Ordinal);

                    sequencePoints[index] = separator >= 0 ? portName[..separator] : portName.Length > 0 ? portName : "m_OptionalFireAndForgetPoint";

                    if (callInfo is { EditorNode: >= 0 })
                    {
                        flowNodes[index] = callInfo.EditorNode;
                    }
                    else
                    {
                        childCursors.Add(index);
                    }

                    break;
                }

                case PulseInstructionCode.SET_VAR:
                case PulseInstructionCode.SET_VAR_OBSERVABLE:
                case PulseInstructionCode.SET_BLACKBOARD_REFERENCE:
                case PulseInstructionCode.SET_VAR_ARRAY_ELEMENT_1D:
                    flowNodes[index] = KnownOrFreshNodeId(LiteralConsumer(index, Reads(instruction)));
                    break;

                case PulseInstructionCode.SET_TEMPVAR:
                case PulseInstructionCode.SET_TEMPVAR_OBSERVABLE:
                {
                    // The first write of a temporary variable is the node declaring it
                    var declaringNode = definition.TempVar(chunkIndex, instruction.TempVarIdx).GetInt32Property("m_nEditorNodeID", -1);

                    flowNodes[index] = declaringNode >= 0 && declaredTempVars.Add(instruction.TempVarIdx)
                        ? declaringNode
                        : KnownOrFreshNodeId(LiteralConsumer(index, Reads(instruction)));
                    break;
                }

                // A method returning its value from reg0 does so through a return node
                case PulseInstructionCode.LOOP_BREAK:
                case PulseInstructionCode.RETURN_VALUE:
                    flowNodes[index] = AllocateNodeId();
                    break;

                case PulseInstructionCode.JUMP_COND:
                case PulseInstructionCode.CHUNK_LEAP_COND:
                    if (!claimedBranches.Contains(index))
                    {
                        AddIf(index);
                    }

                    break;
            }
        }

        private void AssignLibraryInvoke(int index)
        {
            var instruction = instructions[index];
            var registerMap = Binding(instruction).RegisterMap;

            if (forEachInstructions.Contains(index) || arrayAppends.Contains(index))
            {
                inheritsFlowNode[index] = true;
                return;
            }

            // A function whose results are read is a value node, unless it is named for an action
            var funcName = Binding(instruction).FuncName;
            var firstOutput = registerMap.OutParams.Select(static param => param.Register).FirstOrDefault(IsRead, -1);

            if (firstOutput >= 0 && !IsActionFunction(funcName))
            {
                // A value node is invoked again for each node reading it, into the same result register
                if (!valueInvokeNodes.TryGetValue((funcName, firstOutput), out var node))
                {
                    node = KnownOrFreshNodeId(OriginNode(firstOutput));
                    valueInvokeNodes[(funcName, firstOutput)] = node;
                }

                inheritsFlowNode[index] = true;
                valueNodes[index] = node;
                return;
            }

            flowNodes[index] = KnownOrFreshNodeId(LiteralConsumer(index, registerMap.InParams.Select(static param => param.Register)));
        }

        // Steps such as PlayUISoundEvent return a handle the graph may store, so a read result does not make them values
        private static readonly string[] ActionVerbs =
        [
            "Add", "Apply", "Create", "Destroy", "Disable", "Drop", "Emit", "Enable", "Fire", "Force", "Give", "Hide", "Kill",
            "Load", "Play", "Remove", "Reset", "Send", "Set", "Show", "Spawn", "Start", "Stop", "Tag", "Take", "Teleport", "Toggle", "Trigger",
        ];

        private static bool IsActionFunction(string funcName)
        {
            var separator = funcName.LastIndexOf("::", StringComparison.Ordinal);
            var method = separator >= 0 ? funcName[(separator + 2)..] : funcName;

            // Functions returning a bool are prefixed with B, as in BLoadLayoutSnippet
            if (method.Length > 1 && method[0] == 'B' && char.IsUpper(method[1]))
            {
                method = method[1..];
            }

            return ActionVerbs.Any(verb => method.StartsWith(verb, StringComparison.Ordinal)
                && (method.Length == verb.Length || char.IsUpper(method[verb.Length])));
        }

        // A branch running a flow reached from several places calls it, the call naming the branching node and its outflow
        private int OutflowCallOwner(int target, string outflow)
        {
            while (target >= 0 && target < instructions.Count && instructions[target].Code == PulseInstructionCode.NOP)
            {
                target++;
            }

            if (target < 0 || target >= instructions.Count || instructions[target].Code != PulseInstructionCode.PULSE_CALL_SYNC)
            {
                return -1;
            }

            return CallInfo(instructions[target]) is { CallMethodId: < 0 } callInfo && callInfo.PortName == outflow ? callInfo.EditorNode : -1;
        }

        private void AddIf(int branch)
        {
            var instruction = instructions[branch];
            var condition = instruction.Reg0;
            var falseBranch = branch + 1 < instructions.Count && instructions[branch + 1].Code == PulseInstructionCode.JUMP ? instructions[branch + 1].DestInstruction : branch + 1;

            var node = OriginPort(condition) == "not_condition" ? OriginNode(condition) : LiteralConsumer(branch, [condition]);

            if (node < 0 && instruction.Code == PulseInstructionCode.JUMP_COND)
            {
                node = OutflowCallOwner(instruction.DestInstruction, "m_IsTrue");
            }

            if (node < 0)
            {
                node = OutflowCallOwner(falseBranch, "m_IsFalse");
            }

            node = KnownOrFreshNodeId(node);
            flowNodes[branch] = node;

            // The node inverts its condition itself instead of reading a separate Not node
            var test = WriterBefore(condition, branch);
            if (test >= 0 && instructions[test].Code == PulseInstructionCode.NOT && readCounts.GetValueOrDefault(condition) == 1)
            {
                SetOrigin(condition, node, "not_condition");
            }
        }

        // An array node appends its elements one by one to an empty array constant
        private void AddEmptyArray(int index)
        {
            var instruction = instructions[index];
            var constant = definition.Constants[instruction.ConstIdx];

            if (!constant.GetStringProperty("m_Type", string.Empty).StartsWith("PVAL_ARRAY", StringComparison.Ordinal)
                || (constant.TryGetValue("m_Value", out var value) && !value.IsNull))
            {
                return;
            }

            var appends = new List<int>();
            var array = instruction.Reg0;

            for (var i = index + 1; i < instructions.Count; i++)
            {
                var candidate = instructions[i];

                if (candidate.Code == PulseInstructionCode.LIBRARY_INVOKE && Binding(candidate).FuncName == ArrayAppend
                    && Binding(candidate).RegisterMap.TryGetInParam("a", out var input) && input == array)
                {
                    appends.Add(i);
                    array = Binding(candidate).RegisterMap.OutParams.Select(static param => param.Register).FirstOrDefault(-1);
                }
            }

            if (appends.Count == 0 || OriginPort(instruction.Reg0) is not (null or "__empty_array"))
            {
                return;
            }

            var node = KnownOrFreshNodeId(OriginNode(instruction.Reg0));
            SetOrigin(instruction.Reg0, node, "__empty_array");
            valueNodes[index] = node;

            foreach (var append in appends)
            {
                arrayAppends.Add(append);
                valueNodes[append] = node;
            }
        }

        // A property read keeps the name of its component in the origin of the index register
        private void NamePropertyRead(int index)
        {
            var instruction = instructions[index];

            if (OriginPort(instruction.Reg2) != null)
            {
                return;
            }

            var loader = WriterBefore(instruction.Reg2, index);
            var component = loader >= 0 && instructions[loader].Code == PulseInstructionCode.GET_CONST
                ? definition.Constants[instructions[loader].ConstIdx]["m_Value"].ToInt32OrDefault(-1)
                : -1;

            var isVector = chunk.RegisterValueType(instruction.Reg1)
                is PulseValueType.PVAL_VEC2 or PulseValueType.PVAL_VEC3 or PulseValueType.PVAL_VEC3_WORLDSPACE or PulseValueType.PVAL_VEC4;

            if (!isVector || component is < 0 or > 3)
            {
                Report($"Property read {valueNodes[index]} in chunk {chunkIndex} picks its property by an index, the property name is unknown.");
                return;
            }

            SetOrigin(instruction.Reg2, valueNodes[index], VectorComponents[component]);
        }

        private static readonly string[] VectorComponents = ["x", "y", "z", "w"];
    }
}
