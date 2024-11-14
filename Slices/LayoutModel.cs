namespace rt4k_pi.Slices
{
    using Microsoft.AspNetCore.Html;

    public class LayoutModel
    {
        public string Title = "";
        public HtmlString ExtraHeaders = new("");

        public string ButtonClasses { get; } = "w3-btn w3-round-large w3-small w3-light-gray w3-hover-cerulean w3-border w3-border-gray";
    }
}