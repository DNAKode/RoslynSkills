using System;

public class Worker
{
    public void Run()
    {
        Console.WriteLine("Run");
        Run();
    }
}
