namespace Rq1.Fixtures;

public sealed class Alpha
{
    public void Process()
    {
    }
}

public sealed class Beta
{
    public void Process()
    {
    }

    public void Run()
    {
        Process();
    }
}
