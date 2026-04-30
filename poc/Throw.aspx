<%@ Page Language="C#" %>
<script runat="server">
protected void Page_Load(object sender, EventArgs e)
{
    // Throws on every GET request -- no postback required.
    // Used for curl-based XFF trust validation without needing
    // to replay WebForms viewstate.
    throw new System.InvalidOperationException(
        "PoC: test exception for XFF and subnet trust validation.");
}
</script>
