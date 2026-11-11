using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace PcOrg.App.Infrastructure;

/// <summary>
/// Raccolta osservabile che può sostituire tutto il contenuto con <b>una sola notifica</b>.
/// Con ObservableCollection ogni Add avvisa l'interfaccia: riempire un elenco di 2.000 righe significava
/// 2.000 aggiornamenti della griglia. Qui la griglia si ridisegna una volta sola.
/// </summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> items)
    {
        Items.Clear();
        foreach (var item in items)
            Items.Add(item);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
