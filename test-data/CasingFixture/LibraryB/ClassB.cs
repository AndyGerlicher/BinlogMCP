namespace LibraryB;

public class ClassB
{
    public string Name => "ClassB";
    public LibraryA.ClassA Dependency { get; } = new();
}
