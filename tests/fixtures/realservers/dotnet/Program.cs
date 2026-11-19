System.Console.WriteLine(Greeter.Greet("world"));

static class Greeter
{
    public static string Greet(string name)
    {
        var text = "hello " + name;
        return text;
    }
}
