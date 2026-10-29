namespace NekoDex.Core.Enums
{
    public enum SourceProvider
    {
        // Active / Implemented
        NHentai = 0,
        EHentai = 1,
        ExHentai = 2,
        Hitomi = 3,

        // nhentai-Style (REST / JSON-APIs)
        HentaiFox = 10,
        HentaiHand = 11,
        HentaiEra = 12,
        HentaiNexus = 13,
        ThreeHentai = 15, // 3Hentai
        Asmhentai = 18,  // asmhentai.com

        // E-Hentai-Style (GData API / Namespace-Strukturen)
        LoFiEHentai = 30,
        HentaiDB = 32,
        DoujinsCom = 33,

        // Hitomi-Style (Static CDN / Binary Index Parsing)
        SimplyHentai = 50,
        MangaDex = 51,
        KemonoSu = 52, // Kemono (kemono.cr)

        // Custom / Direct Parsing / Fallback
        HentaiEnvy = 72,
        HentaiClap = 73,
        HentaiRun = 74
    }
}
