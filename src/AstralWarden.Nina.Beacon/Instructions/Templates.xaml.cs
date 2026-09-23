using System.ComponentModel.Composition;
using System.Windows;

namespace AstralWarden.Nina.Beacon.Instructions;

/// <summary>Sequencer DataTemplates for the Beacon's instructions (MEF-discovered by NINA).</summary>
[Export(typeof(ResourceDictionary))]
public partial class Templates : ResourceDictionary
{
    public Templates()
    {
        InitializeComponent();
    }
}
