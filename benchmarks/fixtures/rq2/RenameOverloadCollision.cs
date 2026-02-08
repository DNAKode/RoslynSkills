public class Overloads
{
    public void Process(int value)
    {
    }

    public void Process(string value)
    {
    }

    public void Execute()
    {
        Process(1);
        Process("x");
    }
}
