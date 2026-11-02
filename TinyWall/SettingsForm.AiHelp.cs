using System.Windows.Forms;

namespace pylorak.TinyWall
{
    // Special Exceptions > Optional: the "SecureWall AI help" row shows and sets the machine
    // setting AiAssistantEgress, the same flag the AI help settings save. It is not a database
    // profile, so it is never added to SpecialExceptions.
    internal partial class SettingsForm
    {
        private IdWithName? AiHelpItem;

        // Called while the special exception lists are being filled (LoadingSettings is set).
        private void AddAiHelpRow()
        {
            AiHelpItem = new IdWithName(string.Empty, Prompting.AiExplainEgressPolicy.SpecialExceptionName);
            int index = listOptionalGlobalProfiles.Items.Add(AiHelpItem);
            listOptionalGlobalProfiles.SetItemChecked(index, TmpConfig.Service.AiAssistantEgress);
        }

        // True when the item is the AI help row; its new check state then sets the flag.
        private bool TryApplyAiHelpRow(IdWithName item, CheckState state)
        {
            if (AiHelpItem == null || !ReferenceEquals(item, AiHelpItem))
                return false;
            TmpConfig.Service.AiAssistantEgress = state == CheckState.Checked;
            return true;
        }
    }
}
