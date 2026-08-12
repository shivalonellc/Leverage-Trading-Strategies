using System.ComponentModel;


namespace SchwabApiCS.Helper
{
    public class EquityStreamer
    {
        private List<SymbolItem> symbolItems = new List<SymbolItem>(); // list of symbols being watched
        private List<Streamer.LevelOneEquity>? data = null;
        private Streamer streamer;
        private Streamer.LevelOneEquitiesService.EquitiesCallback equitiesCallback;  // method to call when any property changes.

        /// <summary>
        /// Implementation utilizing LevelOneEquity PropertyChanged
        /// </summary>
        /// <param name="streamer"></param>
        /// <param name="callback">method to call when any property changes</param>
        public EquityStreamer(Streamer streamer, Streamer.LevelOneEquitiesService.EquitiesCallback callback)
        {
            this.streamer = streamer;
            equitiesCallback = callback; // called when processing response completed, which could be many symbols
        }

        /// <summary>
        /// Add a SINGLE symbol to watch to an EquityStreamer with no symbol specific callbacks
        /// </summary>
        /// <param name="symbol">symbol to watch</param>
        /// <param name="callback">method to call when LevelOneEquity was changed</param>
        /// <param name="propertyCallback">call this for for every LevelOneEquity that was changed</param>
        public Streamer.LevelOneEquity Add(string symbol)
        {
            return Add(symbol, null, null);
        }

        /// <summary>
        /// Add a SINGLE symbol to watch to an EquityStreamer.
        /// Multiple Adds to the same symbol is supported, and will call multiple callback methods.
        /// When last callback is removed, the streaming for the symbol will be stopped.
        /// </summary>
        /// <param name="symbol">symbol to watch</param>
        /// <param name="callback">method to call when LevelOneEquity was changed</param>
        /// <param name="propertyCallback">call this for for every LevelOneEquity that was changed</param>
        public Streamer.LevelOneEquity Add(string symbol, Streamer.LevelOneEquitiesService.LevelOneEquityCallback callback)
        {
            return Add(symbol, callback, null);
        }

        /// <summary>
        /// Add a SINGLE symbol to watch to an EquityStreamer.
        /// Multiple Adds to the same symbol is supported, and will call multiple callback methods.
        /// When last callback is removed, the streaming for the symbol will be stopped.
        /// </summary>
        /// <param name="symbol">symbol to watch</param>
        /// <param name="callback">method to call when LevelOneEquity was changed</param>
        /// <param name="propertyCallback">call this for for every LevelOneEquity that was changed</param>
        public Streamer.LevelOneEquity Add(string symbol, PropertyCallback? propertyCallback)
        {
            return Add(symbol, null, propertyCallback);
        }

        /// <summary>
        /// Add a SINGLE symbol to watch to an EquityStreamer.
        /// Multiple Adds to the same symbol is supported, and will call multiple callback methods.
        /// When last callback is removed, the streaming for the symbol will be stopped.
        /// </summary>
        /// <param name="symbol">symbol to watch</param>
        /// <param name="callback">method to call when LevelOneEquity was changed</param>
        /// <param name="propertyCallback">call this for for every LevelOneEquity that was changed</param>
        public Streamer.LevelOneEquity Add(string symbol, Streamer.LevelOneEquitiesService.LevelOneEquityCallback? callback, PropertyCallback? propertyCallback)
        {
            Streamer.LevelOneEquity d;
            symbol = symbol.ToUpper();

            if (data == null) // first symbol added, initialize streamer
            {
                data = streamer.LevelOneEquities.Request(symbol, Streamer.LevelOneEquity.CommonFields, equitiesCallback);
                d = data.Where(data => data.key == symbol).Single();
                d.PropertyChanged += PropertyChangedHandler;
                d.Callback = callback;
            }
            else
            {
                d = data.Where(data => data.key == symbol).SingleOrDefault(); // look for existing
                if (d == null) // if symbol not found in data list, add to streamer's list/
                {
                    streamer.LevelOneEquities.Add(symbol);
                    d = data.Where(data => data.key == symbol).Single();
                }
            }

            var si = symbolItems.Where(r => r.Symbol == symbol).SingleOrDefault();
            if (si == null) // new symbol
            {
                si = new SymbolItem(symbol, d);
                symbolItems.Add(si);
            }
            if (callback != null)
            {
                si.Callbacks.Add(callback);  // list of methods to call when equity changes
            }
            if (propertyCallback != null)
            {
                si.PropertyCallbacks.Add(propertyCallback);
            }
            return d;
        }

        /// <summary>
        /// Remove callback from symbol list
        /// Be SURE to use same parameters as was used to Add()
        /// </summary>
        /// <param name="symbol"></param>
        /// <param name="callback"></param>
        /// <param name="propertyCallback"></param>
        public void Remove(string symbol)
        {
            Remove(symbol, null, null);
        }

        /// <summary>
        /// Remove callback from symbol list
        /// Be SURE to use same parameters as was used to Add()
        /// </summary>
        /// <param name="symbol"></param>
        /// <param name="callback"></param>
        /// <param name="propertyCallback"></param>
        public void Remove(string symbol, Streamer.LevelOneEquitiesService.LevelOneEquityCallback callback)
        {
            Remove(symbol, callback, null);
        }

        /// <summary>
        /// Remove callback from symbol list
        /// Be SURE to use same parameters as was used to Add()
        /// </summary>
        /// <param name="symbol"></param>
        /// <param name="callback"></param>
        /// <param name="propertyCallback"></param>
        public void Remove(string symbol, PropertyCallback? propertyCallback)
        {
            Remove(symbol, null, propertyCallback);
        }

        /// <summary>
        /// Remove callback from symbol list
        /// Be SURE to use same parameters as was used to Add()
        /// </summary>
        /// <param name="symbol"></param>
        /// <param name="callback"></param>
        /// <param name="propertyCallback"></param>
        public void Remove(string symbol, Streamer.LevelOneEquitiesService.LevelOneEquityCallback? callback, PropertyCallback? propertyCallback)
        {
            if (data != null)
            {
                var si = symbolItems.Where(r => r.Symbol == symbol).SingleOrDefault();
                if (si != null)
                {
                    var cb = si.Callbacks.Where(r => r == callback).SingleOrDefault();
                    if (cb != null)
                        si.Callbacks.Remove(cb);

                    var pc = si.PropertyCallbacks.Where(r => r == propertyCallback).SingleOrDefault();
                    if (pc != null)
                        si.PropertyCallbacks.Remove(pc);

                    if (si.Callbacks.Count <= 0 && si.PropertyCallbacks.Count <= 0)
                    { // stop streaming this symbol, no callbacks left.
                        streamer.LevelOneEquities.Remove(symbol);
                        symbolItems.Remove(si);
                    }
                }
            }
        }

        public delegate void PropertyCallback(Streamer.LevelOneEquity data, string fieldName);


        /// <summary>
        /// Called by SchwabApiCS Streamer class when equity changes
        /// calls symbol callback for all callbacks in the list 
        /// </summary>
        /// <param name="sender">LevelOneEquity object</param>
        /// <param name="args">has PropertyName that was changed in sender</param>
        public void PropertyChangedHandler(object? sender, PropertyChangedEventArgs args)
        {
            var symbol = ((Streamer.LevelOneEquity)sender).key;
            var si = symbolItems.Where(r => r.Symbol == symbol).SingleOrDefault();
            if (si != null)
            {
                foreach (var pc in si.PropertyCallbacks)
                    pc((Streamer.LevelOneEquity)sender, args.PropertyName);
            }
        }


        public class SymbolItem
        {
            public SymbolItem(string symbol, Streamer.LevelOneEquity data)
            {
                Symbol = symbol;
                Data = data;
                Callbacks = new List<Streamer.LevelOneEquitiesService.LevelOneEquityCallback>();
                PropertyCallbacks = new List<PropertyCallback>();
            }

            public string Symbol { get; set; }
            public List<Streamer.LevelOneEquitiesService.LevelOneEquityCallback> Callbacks { get; set; }
            public List<PropertyCallback> PropertyCallbacks { get; set; }

            public Streamer.LevelOneEquity Data { get; set; }
        }
    }
}
