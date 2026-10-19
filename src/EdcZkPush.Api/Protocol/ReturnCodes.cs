namespace EdcZkPush.Protocol;

/// <summary>Security PUSH Appendix 1 return values.</summary>
public static class ReturnCodes
{
    private static readonly Dictionary<int, string> Codes = new()
    {
        [-1] = "Failed to send the command / parameter error",
        [-2] = "No response to the command",
        [-3] = "Insufficient cache / read-write error",
        [-4] = "Failed to decompress",
        [-5] = "Incorrect length of read data",
        [-7] = "Repeated command (same ID sent twice)",
        [-8] = "Unauthorized connection",
        [-9] = "Incorrect data, CRC failed",
        [-10] = "Incorrect data, failed to resolve data",
        [-11] = "Incorrect data parameter",
        [-12] = "Command execution error",
        [-13] = "Command does not exist",
        [-14] = "Incorrect communication password",
        [-28] = "Door opening command executed during the open time period",
        [-30] = "Integrated template algorithm version is inconsistent",
        [-100] = "Table structure does not exist",
        [-101] = "Conditional field in the table structure does not exist",
        [-102] = "Total field count is inconsistent",
        [-103] = "Field sequence is inconsistent",
        [-105] = "Incorrect data for resolution",
        [-108] = "Invalid OPTIONS",
        [-612] = "Device failed to save the parameter",
        [-614] = "Failed to remotely cancel the alarm",
        [-615] = "Remote restart failed",
        [-616] = "Remotely enabling/cancelling normal open failed",
        [-618] = "Incorrect URL format",
        [-619] = "Firmware internal error: blank input parameter",
        [-620] = "Format of the data delivered by the server is incorrect",
        [-621] = "The table structure does not support this field",
        [-629] = "Incorrect table name",
        [-630] = "Failed to execute the shell command",
        [-631] = "Incorrect base64 length",
        [-708] = "Firmware does not support this command",
        [-709] = "Failed to upload options",
        [-722] = "Failed to upload the data to the software",
        [-725] = "Timed out waiting for the result",
        [-726] = "Server returned an HTTP error",
        [-730] = "No response from the server",
        [-731] = "Server error while receiving the data",
        [-801] = "Incorrect command format (expected cmd:SN:command)",
        [-5000] = "Accepted by master controller, waiting for sub-controller",
    };

    public static string Describe(int code)
    {
        if (code >= 0)
        {
            return code == 0 ? "Success" : $"Success ({code} records)";
        }

        return Codes.TryGetValue(code, out var text) ? text : "Unknown error (see Appendix 1)";
    }
}
