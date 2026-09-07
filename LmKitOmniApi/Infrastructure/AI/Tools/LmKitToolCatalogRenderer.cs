using System.Reflection;
using LMKit.Agents.Tools;
using LMKit.Data;
using LMKit.Model;
using LMKit.TextGeneration.Chat;

namespace LmKitOmniApi.Infrastructure.AI.Tools;

/// <summary>
/// Produces the SAME system/tool-catalog message block that LM-Kit renders for itself on the
/// first turn, so it can be seeded into a NON-EMPTY <see cref="ChatHistory"/> — the one thing
/// standing between this product and function calling that survives past the first message.
///
/// <para>
/// <b>The defect.</b> <c>MultiTurnConversation</c> renders the system block, INCLUDING the
/// registered tool catalog, inside a single <c>if (history.MessageCount == 0)</c> branch taken
/// at <c>Submit</c> time (LM-Kit.NET 2026.9.0, <c>MultiTurnConversation.A(Message, bool, CancellationToken)</c>):
/// </para>
/// <code>
///   bool flag = history.MessageCount == 0;
///   if (flag)
///   {
///       H.D d2 = null;
///       if (toolsEnabled &amp;&amp; Tools.Count > 0) d2 = H.D.A(model, Tools.Tools);   // build catalog
///       string text = template.Render(SystemPrompt, ReasoningLevel);
///       if (d2 != null) d2.A(text, message => history.Add(message));            // system + catalog
///       else if (text != "") history.Add(new Message(System, text));
///   }
///   else
///   {
///       // only refreshes an internal List&lt;string&gt; of tool names; renders NOTHING
///   }
/// </code>
/// <para>
/// Because this codebase rebuilds the <see cref="ChatHistory"/> from stored rows on every turn,
/// that branch is taken exactly once per conversation — the first message. From turn 2 onward
/// the model was never told the tools exist, so it could not call them. Tool PARSING and
/// INVOCATION, by contrast, are gated only on <c>Tools.Count &gt; 0</c> and run on every turn:
/// the catalog text in the prompt is the entire missing half.
/// </para>
///
/// <para>
/// <b>How this type gets the text.</b> The catalog is built by an internal, name-obfuscated type
/// (<c>H.D</c>) with no public equivalent. Rather than re-implement its output — which is
/// per-model and per-<c>ChatToolCallingFormat</c>, and would silently drift — this binds to it by
/// SHAPE, not by name:
/// </para>
/// <list type="number">
///   <item><description>a type <c>T</c> with a static <c>(LM, IEnumerable&lt;ITool&gt;) -&gt; T</c> factory, and</description></item>
///   <item><description>on <c>T</c>, an instance <c>(string, Action&lt;ChatHistory.Message&gt;, IList&lt;Attachment&gt;) -&gt; void</c> emitter.</description></item>
/// </list>
/// <para>
/// Exactly one type in LM-Kit.NET 2026.9.0 matches, and ambiguity fails the bind rather than
/// guessing. The emitter is LM-Kit's own: it decides whether the catalog becomes a
/// <see cref="AuthorRole.Developer"/> message, a <see cref="AuthorRole.ToolsCatalog"/> message
/// (before or after the system message, per the template), or is appended to the
/// <see cref="AuthorRole.System"/> text — all three of which this type therefore gets right for
/// free, on every model, without replicating a single separator.
/// </para>
///
/// <para>
/// <b>Failure is closed, never loud-and-wrong.</b> If the bind fails, the shape changes, or the
/// rendered text does not mention every registered tool, <see cref="Render"/> returns an empty
/// list and the caller falls back to today's behaviour (persona seeded, catalog absent). That is
/// a return to the known defect, not a new one. <see cref="IsAvailable"/> is asserted by
/// <c>LmKitToolCatalogRendererTests</c>, so an LM-Kit upgrade that moves this surface fails the
/// build's tests instead of silently switching function calling back off.
/// </para>
/// </summary>
public static class LmKitToolCatalogRenderer
{
    private sealed record Binding(MethodInfo Create, MethodInfo Emit, Type Renderer);

    private static readonly Lazy<Binding?> Bound =
        new(Discover, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Set once if a bound render call throws, so a broken LM-Kit costs one exception, not one per turn.</summary>
    private static volatile bool _renderFaulted;

    /// <summary>Whether the LM-Kit tool-catalog renderer could be bound in this process.</summary>
    public static bool IsAvailable => !_renderFaulted && Bound.Value is not null;

    /// <summary>
    /// Human-readable bind state, for tests and for a caller that wants to log why function
    /// calling degraded. Never contains user data.
    /// </summary>
    public static string Diagnostics => Bound.Value is { } b
        ? (_renderFaulted
            ? $"bound to {b.Renderer.FullName} but a render call faulted; catalog seeding disabled"
            : $"bound to {b.Renderer.FullName}.{b.Create.Name}/{b.Emit.Name}")
        : "no LM-Kit tool-catalog renderer matched the expected shape";

    /// <summary>
    /// The system-block messages LM-Kit itself would prepend for <paramref name="tools"/>, ready
    /// to be seeded at the head of a non-empty history.
    ///
    /// <para>
    /// Returns an EMPTY list — never a partial or hand-rolled block — when there are no tools,
    /// when the renderer is unavailable, or when the rendered text fails verification. Callers
    /// must treat empty as "seed the plain system prompt instead".
    /// </para>
    /// </summary>
    /// <param name="model">Loaded model. Its chat template decides the roles and ordering used.</param>
    /// <param name="systemPrompt">This turn's system prompt, or null/empty for catalog only.</param>
    /// <param name="tools">Tools that will be registered on the conversation.</param>
    public static IReadOnlyList<ChatHistory.Message> Render(
        LM model, string? systemPrompt, IReadOnlyList<ITool>? tools)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (tools is not { Count: > 0 }) return [];
        if (Bound.Value is not { } binding || _renderFaulted) return [];

        List<ChatHistory.Message> captured = [];
        try
        {
            var renderer = binding.Create.Invoke(null, [model, tools.ToArray()]);
            if (renderer is null) return [];

            Action<ChatHistory.Message> collect = captured.Add;
            binding.Emit.Invoke(renderer, [systemPrompt ?? string.Empty, collect, null]);
        }
        catch (Exception ex) when (ex is TargetInvocationException or MemberAccessException or ArgumentException or InvalidOperationException)
        {
            // The shape matched but the call did not survive. Latch off: the caller degrades to
            // the pre-existing behaviour, which is correct-if-toolless rather than broken.
            _renderFaulted = true;
            return [];
        }

        return Verify(captured, tools) ? captured : [];
    }

    /// <summary>
    /// Every registered tool must be named somewhere in the rendered text, and a system prompt
    /// that was supplied must survive. A block that fails this is discarded rather than sent:
    /// a prompt that advertises the wrong tools is worse than one that advertises none.
    /// </summary>
    private static bool Verify(List<ChatHistory.Message> messages, IReadOnlyList<ITool> tools)
    {
        if (messages.Count == 0) return false;

        var text = string.Join('\n', messages.Select(m => m.Text ?? string.Empty));
        return tools.All(t => !string.IsNullOrEmpty(t.Name) && text.Contains(t.Name, StringComparison.Ordinal));
    }

    /// <summary>
    /// Finds LM-Kit's catalog renderer by signature. Requires a UNIQUE match — two candidates
    /// mean the shape stopped being distinctive, and guessing would be worse than degrading.
    /// </summary>
    private static Binding? Discover()
    {
        Type[] types;
        try
        {
            types = typeof(LM).Assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types.OfType<Type>().ToArray();
        }
        catch (Exception ex) when (ex is FileNotFoundException or BadImageFormatException)
        {
            return null;
        }

        Binding? found = null;

        foreach (var type in types)
        {
            if (type.IsInterface || type.IsAbstract || type.ContainsGenericParameters) continue;

            var create = FindCreate(type);
            if (create is null) continue;

            var emit = FindEmit(type);
            if (emit is null) continue;

            // A second match means the signature no longer identifies one thing.
            if (found is not null) return null;
            found = new Binding(create, emit, type);
        }

        return found;
    }

    /// <summary>Static <c>(LM, IEnumerable&lt;ITool&gt;) -&gt; T</c> on <paramref name="type"/> itself.</summary>
    private static MethodInfo? FindCreate(Type type) =>
        type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .FirstOrDefault(m =>
            {
                if (m.ReturnType != type || m.ContainsGenericParameters) return false;
                var p = m.GetParameters();
                return p.Length == 2
                    && p[0].ParameterType == typeof(LM)
                    // Some enumerable of ITool (so it IS the catalog parameter) that an
                    // ITool[] can be passed to (so Render can actually call it).
                    && typeof(IEnumerable<ITool>).IsAssignableFrom(p[1].ParameterType)
                    && p[1].ParameterType.IsAssignableFrom(typeof(ITool[]));
            });

    /// <summary>Instance <c>(string, Action&lt;ChatHistory.Message&gt;, IList&lt;Attachment&gt;) -&gt; void</c>.</summary>
    private static MethodInfo? FindEmit(Type type) =>
        type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .FirstOrDefault(m =>
            {
                if (m.ReturnType != typeof(void) || m.ContainsGenericParameters) return false;
                var p = m.GetParameters();
                return p.Length == 3
                    && p[0].ParameterType == typeof(string)
                    && p[1].ParameterType == typeof(Action<ChatHistory.Message>)
                    && p[2].ParameterType == typeof(IList<Attachment>);
            });
}
