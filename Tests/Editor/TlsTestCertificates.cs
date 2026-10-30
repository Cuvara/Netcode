namespace Cuvara.Netcode.Tests.Editor
{
    /// <summary>
    /// Two fixed self-signed test certificates (CN=localhost, SAN DNS:localhost, RSA 2048,
    /// valid until 2126) with their private keys, as PKCS#12 with an empty password.
    /// </summary>
    /// <remarks>
    /// Fixed rather than generated because Unity's Mono profile compiles
    /// <c>CertificateRequest</c> but cannot load it at run time (TypeLoadException in CI),
    /// so a certificate cannot be created in the test. Test material only: the keys protect
    /// nothing, and no production code or pin refers to them. PKCS#12 is encrypted with
    /// SHA1/3DES, the scheme Mono's importer reads. Made with
    /// <c>openssl req -x509 -newkey rsa:2048 -nodes -days 36500 -subj /CN=localhost
    /// -addext subjectAltName=DNS:localhost</c> then <c>openssl pkcs12 -export -certpbe
    /// PBE-SHA1-3DES -keypbe PBE-SHA1-3DES -macalg sha1</c>.
    /// </remarks>
    internal static class TlsTestCertificates
    {
        internal const string ServerPfxBase64 =
            "MIIJYQIBAzCCCScGCSqGSIb3DQEHAaCCCRgEggkUMIIJEDCCA8cGCSqGSIb3DQEHBqCCA7gwggO0AgEAMIIDrQYJKoZIhvcN" +
            "AQcBMBwGCiqGSIb3DQEMAQMwDgQIkj3pI4sbGnkCAggAgIIDgMKyHpPYELbliKudM30IlNURCgr4Ae6Ultv8sxLGX/9lrnQP" +
            "Pd9LaxVhZ9/CYubAHavfqcGPdaKp9Yqko9ARuiMxKDcy0PwbFci1oOpQUgX/9DAbvA/psgn/sLn4QolPcdSGfqUU6i0LTtmU" +
            "o9jK8sYjZROxawAmMWIqZm2B0ib4t1rX/bcMMd5qE68wZhW0v/IXbyuPN35mUyeyr7yUAp/ONDqzXTRdOrsjleBDMT6kAMf8" +
            "7FUDT1RflUIuesKnLdOpFAzc95+D4/A47p+McTw9BsXL7kSJjxBpQemidx1K2NpWI2HzxSVraWehsayP6v4pbaPGe7j3DQ4k" +
            "5hemJvnLR9TtYTihNg/0XRAQXabk5mMAtZx7clbeFkOeYAeCNHpHNW5BYcpPaUI4FFwIbe8v7ID8qNbaImJz5KiWOb60EI7e" +
            "Fz4p8RE+pZoRzRlztThDcnQaPv4aR6kr9wXRK3zc8dLZ1as8Fix4Y3Oe1/7zv5DF+kQfuxJyivTxoDSQer0jB2uZpGe6i8ba" +
            "Tv+HpP18mb6Zss2rL4jyFP24Olp+6I0IFcjEnk/NU+bqPFL4kG/W+kFQEZnaeiKKRJ2gdO8oFWTe+p/H5oVnhaFjbZU6dH+m" +
            "BHzXayglpbH6aEAlMVO8O9csx9eeGGSB6xxwvXOL5zcMgcouHiQ2zSLImXTiDHk3KAYuGxDbXYlz8P7Cq13d5Pe+TTFchOtm" +
            "u4Gbbr2T/n7CzY74c26EGdzACjNf1YOk3os6WFOd8BJz1QPt9em5qb+X9NSwf4iwn7aWoTgyaHR8CjpWcqbLSqAE/cfccnvV" +
            "TTfZvjSSsW1V1qV9Ia3RyqPV6dcDPr29ISc6GWkFa1eYtz+cwr7Vc2VmtbwyGnua1XmVSO1rP51YWsDyjq3t2PSpJg9AULga" +
            "mT+MCWnQsPqzUkeyRgztxXNkiuLCp973QQfe/sGjxqCtGe3hSdCNsNEmYJedGom9M1G3yiK6tZo5vSFejkmabPOLEc860Y+k" +
            "i7iOij9cz3CckTLTR222yB7PFFG/x6iSizvWfO8Ch7wUn8glQQWCV2qU3zvXvmh6TfBEjrcz0DxbWe1VsaMF7k9dIAnE0pcx" +
            "4A5laTbvWpDl6n++3VWiLSUelKvoVveq8X98BSldTZlTifV8a877xWQ5k/mu5cK4dODTqYajRqqQXFDfr0+EE+huvMXgMIIF" +
            "QQYJKoZIhvcNAQcBoIIFMgSCBS4wggUqMIIFJgYLKoZIhvcNAQwKAQKgggTuMIIE6jAcBgoqhkiG9w0BDAEDMA4ECCf0HAdQ" +
            "Wxt/AgIIAASCBMgV3ciosvj/x/wx+Sldt88OhDb+5a3F4uf2leB/zDBzGOvPC4gZBufw7j+jjb9K1VXj2sIynNw7zrY5WEhc" +
            "SpHQAdL0OPgxTlg0zk0AKu92YWl30PKr46k1VOWsh/7GOaxFTWhnlNEcQLMT0y7mIpdJpwtxUNSWVMd2FBTKV87K6KmaS/Y6" +
            "A6g5DmT7Qtryza0LuBw8xyfjuwnZKgmRXKta6tbbLuHqW4aUkE7yHzRzNrAAN7aIrgDg5GT//B8WXSU1Ln0jcfunWtbvqze7" +
            "MCgGm9ETNwu30YJv6utLt7aX4Q7UCGh/kyWfystKbEkYBretm9hnIckul3MWjhHEVDBndIPC687b0F7jOVZIknAvdSmKyQH8" +
            "rgK3v08QvTJATXHxHgpJLbDkzFKZpv9rjTB47I1kbtoGave+1mBOzzTQbQSsqbDTWFqYich7CkADlTIiynf+Qip/4Cj1mxiT" +
            "dKAdXKQquEkqAUw+wxBtF/jLfUcMtdZAmTvcOaPSUfgYOSmtFWrrYxFBt52AFIX4QA6x5ZPBbPNTgE1kbeCeWTdf6FYL0qUs" +
            "VjX2K/au9h/FGw0oHD8jImyYDvE/AogZPUfnUWhAxvkDnMid0Tvdp8i1xwutnT4GatEsiLrs8LUCWIJJAlzflTEa2itc430Y" +
            "zwn2fhXDaFFEOkn9fr7EllSHkTbm/x7LtB7SRcUc8OgmGOQuEonQ6aPYm1SKC9fyg6rj0fpfV9xDD4QV7cyhAZAAgwAahWcR" +
            "zCmb+QjzYu3F8usOFHgAX23padTVeFKjB0QB++l9N4Ch4ScMwlCYVTYoS1u/RChFyg2gsiBsEAeG5MJOMxnV2RVXXxqJsWp2" +
            "2aO+q+sNTjjJxAKbvb5qGceL9hEqzCnz3kohXHpYEzIrn3bp2H+uibQHg6zucKwTQA6PodQgr09caYkzW8pHPCE1EEOYm6wF" +
            "cosgtOiz6aZwUMoKiWg27iVwcCaXJnsysrvbIMVAQ6pYxyXa3tAY2K9FU+lw+J6WebfBWWuqugg0t0WGcA6el5giQF9x8If0" +
            "4UXCikOe3bgbQg3PYODuC02PHGtXRx07xCAmN1mdbN8KEfu3aqJFf1FaXIDaKGKLNgNmkHkCuprOpbSVOy/04vTvOGONADF/" +
            "ku0n3kSmg+1059ZCoIGz9e5l0ozF+XHJzZwqKsnyE9cKa4NEGAoQHOfEfJajVV/cuM3bvi07hfmesVCUDeE1h0R23AvnnLLy" +
            "0aWQbdb3a8f0+EAMADnqKQK/DqWyASipaQGs4h95BkKkIJNJZ+uPSJwSGJW2IeKgVJphoiWsfwuBgnZknbnsM/J921zQaoPW" +
            "Gh5NWHukj87gOpyrA/trVWSopXetMEezjO4djedzWRqwBaqhbK1w0drr+pQpVuSuJs37LMmvPq5F3xFjwWgq3UQt0E4cRL93" +
            "qFwn3B+G+I7dYEDj8d0xV0iMCDfSu4DfMkuEU8sN1Ib1DISK0DsxQxO07HP4dYDrst0SFY+GWXtDpbmBz9Y/Y+s0FIA9Zaua" +
            "SHgK35KeGJzUvZ2AV+nYEZTtgT7+hFPP4ddmgelWNyeqgC6NLtofjxTPYmR28p6kGqZEgVt7ujm5XOnsRI/cyCvp1H8KYjAg" +
            "KJVUqpEkASFJhrMxJTAjBgkqhkiG9w0BCRUxFgQUmpOJm4L6I6Pas/v1ExP7uCRPif8wMTAhMAkGBSsOAwIaBQAEFI1DRYC/" +
            "NiEwzqZoX6zYIkbqeVgnBAg5VxXZRAUrcgICCAA=";

        internal const string OtherPfxBase64 =
            "MIIJYQIBAzCCCScGCSqGSIb3DQEHAaCCCRgEggkUMIIJEDCCA8cGCSqGSIb3DQEHBqCCA7gwggO0AgEAMIIDrQYJKoZIhvcN" +
            "AQcBMBwGCiqGSIb3DQEMAQMwDgQIwcEU0/ael+gCAggAgIIDgJ3daP5mPce/mo7DQMt3jptnKBycyiVFMI14nRDHDSLwFrNf" +
            "DH+n5ZMpLFf2RblcpM+IPtgRYevZjIpHUcfSpeso8d5JEhX4xA2uKOqr/224hXwBLlrryac5ZHcO9xD1BhL/Ra3TrFpZia4Q" +
            "zMyYDCKnyR4Zk1CZttIGt69ktiMjb2Ih5CzplGvNPsBFGexLOePnGeUmXdhS+DsPSf7DDwebT8ObDZB24E0Jsk88qxn/NjDG" +
            "HX05M2cGdXeHVbnB6diLDo/a5jVuoA7JPnnHYD2m2McrtXJkodhYYze6xRLQzCdDe7YEIEukIDaI7dsLtaqxgdPrEvj6yDSd" +
            "bSzLGSC+M7A7IGWEQcN0n8Ru7RNO9gbBJkHnIw2JGiWoS0I2lFuPR7FZ4aMYrqGAWxof3DGtKTo7gav02sNJPkLNj4M4W9c/" +
            "9WEf49nCWH6zKpHS/eVKE9Xn6ZfGUIZMCLOPLRzCneCCDtjW6inZzOFRC7EK76X6A6MoLQ3TBEMd2yM6b4Wm2RlFUyA/jqSa" +
            "07vhydwgje4eMWg88oF32R1y1UkBKMdLbKFuxzq8ZqvtwBlftUdgx1kq+WfcfMZWbLEGTPqXKl8mW+lsNC9jHCUONsRi7utN" +
            "SbWW3GK8gUDrOO/7duLydwTFK4anTRUJkNCyjagvvNjOZd1yfcuZ+mUdE7CZX4NW3Mp79DZ9hah0hAyqM1hPfPs/QbLr6FHH" +
            "/0giAJGVkTWcla+mfjQaYFkRhQOhwqgG9ZE1f2pwGFfLVIw15jfcdIR3tyWmqHC4nzbJHchbUZkU3+6QrKw+kU0P4ue76Llv" +
            "zeFckIf28+ti/zJ4E91XKUrxqyh2fJ7506CIs+qYw8So+AEPbx8sRYPsEX8JkzE6W24yJzply74lQ3AblkzTLBb13cr4G/ba" +
            "p5k3B78Vx4crsLOl/S3qdtet12Cd9V+vJc8OozMEe5YN/ITHks40qAn1qx99+qkuqOf6H/ZC4Q+7NQr51yNgXzbWp3CJpmgB" +
            "Zv7lYvBNjG5WN3wa9pOJ8E2Wtpw95PNnPtrg0SKAbz+CkK8/J0q7LeNp30XTV7pg8kf6BLmOtQH2yzY8ZPEuubZXSqtwe2W3" +
            "JBfjNoeSFZ3gs35Axzd9zLNzOEmwNo/o27Gfz0q8U3TaFfCLYost0WRliofgBIO70WvesD6b8VdRIESe8tnCJry/+gq0MIIF" +
            "QQYJKoZIhvcNAQcBoIIFMgSCBS4wggUqMIIFJgYLKoZIhvcNAQwKAQKgggTuMIIE6jAcBgoqhkiG9w0BDAEDMA4ECD2XhKGk" +
            "C4C7AgIIAASCBMgPBVLyYDBDcbX7RCgWMuP+ZS+X8pXLFqViypP0SCxNkTaASly2HMmaKgrWj2m6WtK2h3Gk5IJH6MeA0HEV" +
            "bl3+jpZB4jykcraXjKa7aYNJzdIk/OerqZd1pQLdovEd6hpbj3JLFnDkdZL11Ie+Jf03K5hHukLA9MCWFF6g/ovmKOksf23w" +
            "VyPZRx02xx2+WT5RK4hZG6d0G6SRJceshk+XXXdU2VztNpK5JhfrymBIATaNEWZMVkE15JtwECNkPjQdDwJdo6ldu3jRrehn" +
            "gjfMZ/cVTCR+FWSZKAts0+IjCARxQbNtPNVundrq05GoOpZOUUJKX6yqwHJ0+D6h+DKymdkhf7oHq9pinZxDBYBfs0UIPmVg" +
            "nAl55b/0izbFsaqZAGhNPDzM9MKpRTALvuCBTLASII0pQYn4VB0uMKWDVsTpZyaisBdsubCsVOmrW3yXFmEeZrg92pwx3Trk" +
            "44PKdUXf3Bwiuyu6Gafm2OO+ur9dJRNzNjTclrJiHTgZpxvZ7nr/DiLy/RJ5f0p27DOvWfLkgnAjI+obqi6X0rWFYjcuNOM5" +
            "LeqXuUwBNgolgBiMRfybucoy/lH0ASJkwUP423H+O2QmI2/gf1Se20VSZ/MS6o75obMy3Vabi/2Hzb9nzcg43auLEBppV5uM" +
            "ibkp7x9aWOYT0kC7sTDoQx84Rz6IVtB7JM8G/oJAeDOKvk0n4XMEFJAi3hSIufFoSdnGW9K69IEL9VFejw2/LWdDxYwCDlxl" +
            "px1tK2m3IqivO9vjfltLbRzxEZxRPRcUHcYJR3Hv0kPZbaCzaqjLIpKKIBwp4ypPi014drdN126lDdCODNFsOQh0hn4izIwa" +
            "vdBiS6VNsV2bi0fzFMiGwnz6visA8O9dxyyVxbqUg6fy7yPxhpQJqf0vUHhYFMHffu+fhvbX8hq/vk+3Qsgc+M7KE09gh3Tn" +
            "U8LN3hTTCaO/BQELrlK1Uk0QT6tGCL1PxZr2SkD21TwKxK6lh1HPGLF7WD34w4i7pzwn+OTEthIa/DXT3kEE3vfN7L5RyvnO" +
            "iJ25mFrMo6rF5wtHxPZrDDFOc1uvO4EqIM1nqTfkESDPh1mIztlN+ibT/blmOu5JmqlDWEoHax3bw+eN7Y8p/XE6H4/8zcmB" +
            "eC4AzcbUsETRdNHV2TFSAZIxFm50+qBqd5o0J4/Wna5LvbEiaLDTs3Xi2IEB/uHjzq+hUO5tHIzP/e/Sx8B8jOB06j4tZwWt" +
            "fLDBNUVzB5IlSlvNpuji78v7OfQE6hYiuAqtIo0LYvdg67HocLiU+jAkARRPNZpfF/3o0wf+yf12FFETpEsiMsqMir9vhu3v" +
            "UL+lL7jok4yLCNoUWDmt3wyw+zoS+t7n2XbBYTnhknV1V68hNw0mWqFWXQMPD2bPsTvThOX2VfY3mtcfNrYz/jIhzHsG3lOJ" +
            "/YgF+AsiAyGbHjt/stnfmMakGr96hxKqRGr/5/eO+jURcAB5K1VgubBWxr4V2MbRah4p7JubP0TBBw+3rRbtJtm/vqWUXWA7" +
            "seP8yki6ChNzWSJ2OE0sDigbubRZhTpVEgtrJ6hW/13VGq+ppFLjOg6VVjJCo2qPvVTAK7297HYwx7SULFX+IgHznwyqMRDx" +
            "Hqt4J32cklFq51MxJTAjBgkqhkiG9w0BCRUxFgQUJsm1BRYuvo2q4+wz2eoWxfZVqsgwMTAhMAkGBSsOAwIaBQAEFKPlL/Jl" +
            "RYtAjxPS/1J31t9sVDO4BAj4nGhHLv8bjAICCAA=";
    }
}
