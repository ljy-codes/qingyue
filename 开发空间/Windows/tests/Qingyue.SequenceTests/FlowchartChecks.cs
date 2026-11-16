using Qingyue.Core;

internal static class FlowchartChecks
{
    internal static void Run()
    {
        const string source = """
            flowchart TD
            A[开始] -->|下一步| B(处理)
            B --> C{成功?}
            C -->|是| D((结束))
            C -.->|否| B
            """;
        Check(MermaidFlowchart.TryParse(source, out var diagram, out var error),
            "flowchart 基本语法应可解析：" + error);
        Check(diagram is not null && error is null, "成功解析不能返回错误或部分结果");
        Equal(MermaidFlowchartDirection.TopDown, diagram!.Direction, "TD direction");
        Equal("A,B,C,D", string.Join(",", diagram.Nodes.Select(node => node.Id)), "first-seen node order");
        Equal(MermaidFlowchartNodeShape.Rectangle, diagram.Nodes[0].Shape, "rectangle shape");
        Equal(MermaidFlowchartNodeShape.Rounded, diagram.Nodes[1].Shape, "rounded shape");
        Equal(MermaidFlowchartNodeShape.Diamond, diagram.Nodes[2].Shape, "diamond shape");
        Equal(MermaidFlowchartNodeShape.Circle, diagram.Nodes[3].Shape, "circle shape");
        Equal("下一步", diagram.Edges[0].Label, "edge label");
        Check(diagram.Edges[3].Dashed && diagram.Edges[3].From == "C" && diagram.Edges[3].To == "B",
            "dashed edge direction and style");

        const string duplicate = """
            graph LR
            start[旧名称] --> finish[[完成]]
            start[开始]
            """;
        Check(MermaidFlowchart.TryParse(duplicate, out var horizontal, out var duplicateError),
            "graph LR 和重复节点声明应可解析：" + duplicateError);
        Equal(MermaidFlowchartDirection.LeftRight, horizontal!.Direction, "LR direction");
        Equal("start,finish", string.Join(",", horizontal.Nodes.Select(node => node.Id)), "duplicate order");
        Equal("开始", horizontal.Nodes[0].Label, "duplicate declaration updates label");
        Equal(MermaidFlowchartNodeShape.Subroutine, horizontal.Nodes[1].Shape, "subroutine shape");

        string unchanged = source;
        _ = MermaidFlowchart.TryParse(source, out _, out _);
        Equal(unchanged, source, "parser does not mutate source");

        Reject("flowchart TD\nsubgraph group\nA --> B\nend", "subgraph");
        Reject("flowchart TD\nA[缺少右括号 --> B", "格式");
        Reject("sequenceDiagram\nA->>B: 错误图类型", "flowchart");
        Reject("flowchart TD\nA -->|未闭合 B", "边标签");
        Reject("flowchart TD\n" + string.Join("\n", Enumerable.Range(0, 101).Select(i => $"N{i}[节点]")),
            "100");
    }

    private static void Reject(string source, string fragment)
    {
        bool parsed = MermaidFlowchart.TryParse(source, out var diagram, out var error);
        Check(!parsed && diagram is null && error?.Contains(fragment, StringComparison.Ordinal) == true,
            "应拒绝流程图输入并说明原因：" + error);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Equal<T>(T expected, T actual, string message) =>
        Check(EqualityComparer<T>.Default.Equals(expected, actual),
            $"{message}: expected '{expected}', got '{actual}'");
}
