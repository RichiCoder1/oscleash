namespace OSCLeash.App.Services.Leash;

internal enum LeashDirection
{
    Unknown,
    North,
    East,
    South,
    West,
}

internal record Leash(
    string Name,
    LeashDirection Direction = LeashDirection.Unknown,
    bool IsGrabbed = false,
    float Stretch = 0.0f,
    float XPos = 0.0f,
    float YPos = 0.0f,
    float ZPos = 0.0f,
    float XNeg = 0.0f,
    float YNeg = 0.0f,
    float ZNeg = 0.0f)
{
    public Leash WithParameter(string parameterKey, object? value)
    {
        return value switch
        {
            float floatValue => parameterKey switch
            {
                "Stretch" => this with { Stretch = floatValue },
                "X+" => this with { XPos = floatValue },
                "Y+" => this with { YPos = floatValue },
                "Z+" => this with { ZPos = floatValue },
                "X-" => this with { XNeg = floatValue },
                "Y-" => this with { YNeg = floatValue },
                "Z-" => this with { ZNeg = floatValue },
                _ => this
            },
            bool boolValue => parameterKey switch
            {
                "IsGrabbed" => this with { IsGrabbed = boolValue, Stretch = boolValue ? Stretch : 0.0f },
                _ => this
            },
            _ => this
        };
    }
}