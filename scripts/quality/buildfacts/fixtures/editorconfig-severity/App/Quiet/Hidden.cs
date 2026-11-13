namespace Fixture;

public static class Hidden
{
    // CS0168 (declared, never used) is an error here (TreatWarningsAsErrors) unless something silences it.
    public static int Run()
    {
        int unused;
        return 1;
    }
}
