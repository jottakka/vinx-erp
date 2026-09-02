using System.Reflection;
using System.Xml.Linq;
using Vinx.Erp.Foundation.Persistence;
using Xunit;

namespace Vinx.Erp.ArchitectureTests;

/// <summary>
/// Matriz de dependências entre Foundation, módulos e hosts. Substitui as
/// checagens de contenção de string: os módulos são descobertos em src/Modules
/// (um novo módulo entra na matriz sozinho) e as arestas são lidas do csproj
/// (referências diretas exatas) e dos assemblies carregados (camada de
/// referências de assembly).
/// </summary>
public sealed class ModuleDependencyMatrixTests
{
    private const string FoundationAssembly = "Vinx.Erp.Foundation";

    private static readonly string[] HostAssemblies =
    [
        "Vinx.Erp.ApiHost",
        "Vinx.Erp.WorkerHost",
        "Vinx.Erp.McpHost",
    ];

    // Arestas diretas permitidas entre módulos (além de Module → Foundation).
    //
    // AI → Paguai: os guardrails do agente persistem a proposta tipada com SQL
    //   cru no schema de notificações/paguai — cicatriz conhecida do legado,
    //   mantida de propósito até o raw-SQL virar função/contrato de módulo.
    // Notifications → AI: o adapter do canal do agente consome os contratos do
    //   runner (InboundAgentRequestReceived, AgentChannelKind).
    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> AllowedModuleEdges =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            ["Vinx.Erp.AI"] = new HashSet<string>(StringComparer.Ordinal) { "Vinx.Erp.Paguai" },
            ["Vinx.Erp.Notifications"] = new HashSet<string>(StringComparer.Ordinal) { "Vinx.Erp.AI" },
        };

    public static TheoryData<string> ModuleNames
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var moduleName in DiscoverModuleNames())
            {
                data.Add(moduleName);
            }

            return data;
        }
    }

    [Fact]
    public void Modules_exist_in_the_source_tree()
    {
        var modules = DiscoverModuleNames();
        Assert.NotEmpty(modules);
        // Foundation não é um módulo e não pode aparecer na pasta de módulos.
        Assert.DoesNotContain(FoundationAssembly, modules);
    }

    [Theory]
    [MemberData(nameof(ModuleNames))]
    public void Module_project_references_stay_within_the_allowlist(string moduleName)
    {
        var directReferences = ReadDirectProjectReferences(moduleName);

        var allowed = AllowedEdgesFor(moduleName);
        var offending = directReferences
            .Where(reference => !allowed.Contains(reference))
            .ToArray();

        Assert.True(
            offending.Length == 0,
            $"{moduleName} has direct module references outside the allowlist: " +
            string.Join(", ", offending) +
            $". Allowed: {string.Join(", ", allowed.OrderBy(name => name, StringComparer.Ordinal))}.");
    }

    [Theory]
    [MemberData(nameof(ModuleNames))]
    public void Module_assembly_references_stay_within_the_allowlist_closure(string moduleName)
    {
        var assembly = LoadAssembly(moduleName);
        var referenced = assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .OfType<string>()
            .Where(name => name.StartsWith("Vinx.Erp.", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);

        // Nenhum módulo referencia um host: hosts são transporte + chamadas de
        // módulo; a seta módulo → host inverteria a composição.
        var hostReferences = referenced.Intersect(HostAssemblies).ToArray();
        Assert.True(
            hostReferences.Length == 0,
            $"{moduleName} must not reference transport hosts: {string.Join(", ", hostReferences)}.");

        // Referências de projeto transitivas vazam para a tabela de referências
        // do assembly; por isso a camada de assembly é validada contra o
        // FECHAMENTO da allowlist. A aresta DIRETA exata é provada pelo teste
        // Module_project_references_stay_within_the_allowlist (sem transitivos).
        var allowed = AllowlistClosure(moduleName);
        var offending = referenced
            .Where(name => name != moduleName && !allowed.Contains(name))
            .ToArray();

        Assert.True(
            offending.Length == 0,
            $"{moduleName} references assemblies outside the allowlist closure: " +
            string.Join(", ", offending) +
            $". Allowed closure: {string.Join(", ", allowed.OrderBy(name => name, StringComparer.Ordinal))}.");
    }

    [Fact]
    public void Foundation_does_not_reference_modules_or_hosts()
    {
        var referenced = typeof(FoundationDbContext).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .OfType<string>()
            .Where(name => name.StartsWith("Vinx.Erp.", StringComparison.Ordinal))
            .ToArray();

        Assert.True(
            referenced.Length == 0,
            $"{FoundationAssembly} must not reference modules or hosts: {string.Join(", ", referenced)}.");
    }

    private static IReadOnlySet<string> AllowedEdgesFor(string moduleName) =>
        AllowedModuleEdges.TryGetValue(moduleName, out var edges)
            ? edges
            : new HashSet<string>(StringComparer.Ordinal);

    // Fechamento transitivo da allowlist a partir do módulo: Module → Foundation
    // está sempre presente; as arestas permitidas se compõem (Notifications →
    // AI → Paguai → Foundation, por exemplo).
    private static IReadOnlySet<string> AllowlistClosure(string moduleName)
    {
        var closure = new HashSet<string>(StringComparer.Ordinal) { FoundationAssembly };
        var queue = new Queue<string>([moduleName]);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!AllowedModuleEdges.TryGetValue(current, out var edges))
            {
                continue;
            }

            foreach (var edge in edges)
            {
                if (closure.Add(edge))
                {
                    queue.Enqueue(edge);
                }
            }
        }

        return closure;
    }

    private static string[] DiscoverModuleNames() =>
        Directory
            .GetDirectories(Path.Combine(RepositoryRoot(), "src", "Modules"))
            .Select(directory => new DirectoryInfo(directory).Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

    // Arestas DIRETAS do csproj do módulo: cada <ProjectReference> cujo caminho
    // aponta para outro módulo. Foundation e hosts são nomeados à parte.
    private static IReadOnlySet<string> ReadDirectProjectReferences(string moduleName)
    {
        var projectPath = Path.Combine(
            RepositoryRoot(), "src", "Modules", moduleName, $"{moduleName}.csproj");
        var document = XDocument.Load(projectPath);
        var references = document
            .Descendants("ProjectReference")
            .Select(element => (string?)element.Attribute("Include"))
            .OfType<string>()
            .Select(path => Path.GetFileNameWithoutExtension(path))
            .Where(name => name.StartsWith("Vinx.Erp.", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);

        // Referências a Foundation são a base permitida de qualquer módulo e
        // saem do conjunto; o que sobra são apenas arestas entre módulos.
        references.Remove(FoundationAssembly);
        return references;
    }

    private static Assembly LoadAssembly(string moduleName) =>
        AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(assembly => assembly.GetName().Name == moduleName)
        ?? Assembly.Load(moduleName);

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Vinx.Erp.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate Vinx.Erp.sln.");
    }
}
