using ExternalContract;
namespace Fixture;
[Obsolete("Fixture attribute for metadata extraction")]
public class Widget<T> : ExternalBase, IExternal where T : class, new()
{
    static Widget()
    {
        var marker = Environment.GetEnvironmentVariable("DLL2LLM_FIXTURE_MARKER");
        if (marker != null) File.WriteAllText(marker, "TARGET CODE EXECUTED");
    }
    public T Value { get; private set; } = new();
    public string ReadOnly => "example";
    public int this[int index] { get => index; set { } }
    public event EventHandler? Changed;
    public const int Answer = 42;
    public T Echo(T value) => value;
    public U Map<U>(T value) where U : new() => new();
    public void Overload(int value) { }
    public void Overload(string value) { }
    public int Optional(int value = 7) => value;
    public ExternalValue External(ExternalValue value) => value;
    public int[] Transform(in int value, out bool ok, params string[] labels) { ok = true; Changed?.Invoke(this, EventArgs.Empty); return [value]; }
    protected void ProtectedMember() { }
    private void Hidden() { }
    public class Nested { }
    internal class HiddenNested { }
}
public enum Mode : short { Ready = 1 }
public readonly struct Position { public int X { get; } }
public interface IContract { int Read(); }
public delegate int Callback(int value);
internal class InternalContainer { public class PublicInsideInternal { } }
