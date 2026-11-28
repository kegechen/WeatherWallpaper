.pragma library
.import "zh.js" as Zh

// 搜城市：照搬 Windows 版 WeatherService.SearchCity（两边改的时候一起改）。
// 两个免费地名库一起查、合并排序：
//  · Photon（OpenStreetMap 数据）：国内的区、县、镇、街道、景区都有中文名，但同名的村子很多、它自己的排序不分级别；
//  · Open-Meteo（GeoNames 数据）：外国城市有中文名（巴黎、伦敦），但国内很多区县搜不到（尤其带"区""市"后缀时）。
// 排序：名字对得上（"黄陂"也算对上"黄陂区"）→ 行政级别高（市 → 区县 → 镇 → 村）→ 人口多 → 各自原来的顺序；
// 同一地点（同名同地区、同名相距 30 公里内，或相距 8 公里内、名字像是同一个地方，见 samePlaceName）只留一个。名字一律转成简体显示。
// QML 的 XMLHttpRequest 没有超时，也没有 setTimeout：8 秒到点由设置窗口的计时器调 expire()，
// 一个回来了 = 不等慢的那个；都没回来 = 按超时报错。

var zh = Zh.wwpZh
var OPEN_METEO = "https://geocoding-api.open-meteo.com/v1/search?count=10&language=zh&format=json&name="
// 只要行政区和居民点（外加山峰），不要小区、工厂、商店这些
var PHOTON = "https://photon.komoot.io/api/?limit=30&osm_tag=place&osm_tag=boundary:administrative&osm_tag=natural:peak&q="
var AdminSuffix = ["市", "区", "县", "镇", "乡", "街道", "地区", "林区", "新区", "自治县", "自治州", "自治区", "盟", "旗", "省"]
var MAX = 8

// 开始搜 name。done(err, list) 只调一次：err 是出错原因（"" = 没出错），list 是 [{ name, detail, lat, lon }]，最多 8 条。
// 返回 { expire, cancel }：expire = 到点了，有什么用什么；cancel = 不要了（又搜了别的 / 切走了），之后不再调 done
function search(name, done) {
    var raw = str(name).trim()
    var q = zh.simplified(raw)
    var reqs = [], over = false
    var srcs = []   // 两个库各一项 { state, list, err }，下面开查时填上

    function get(url, lang) {
        return new Promise(function (resolve, reject) {
            if (over) {
                reject(new Error("已取消"))
                return
            }
            var x = new XMLHttpRequest()
            reqs.push(x)
            x.onreadystatechange = function () {
                if (x.readyState !== XMLHttpRequest.DONE)
                    return
                if (x.status === 200)
                    resolve(x.responseText)
                else
                    reject(new Error(x.status ? "HTTP " + x.status : "网络不通"))
            }
            x.open("GET", url)
            // Photon 按这个头给中文名或英文转写；不带时 QML 默认发 en，国内区县全是英文名，中文词搜不到（Windows 的 HttpClient 默认带系统语言，不用设）
            if (lang && x.setRequestHeader)
                x.setRequestHeader("Accept-Language", lang)
            x.send()
        })
    }
    function stop() {
        over = true
        reqs.forEach(function (x) { if (x.readyState !== XMLHttpRequest.DONE) x.abort() })
    }
    function finish() {
        if (over)
            return
        stop()
        var all = [], err = null, ok = false
        srcs.forEach(function (s) {
            if (s.state === 1) {
                ok = true
                all = all.concat(s.list)
            } else if (s.state === 2) {
                err = err || s.err
                console.log("city search: " + s.err.message)
            } else {
                console.log("city search: timed out")
            }
        })
        // 有一个源成功但没结果不算失败，按"没有找到"处理
        if (all.length === 0 && !ok) {
            done(err ? err.message : "搜索超时，稍后再试", [])
            return
        }
        var list   // rank 出异常也不能把 done 吞掉，不然设置窗口的 searching 永远清不掉
        try {
            list = rank(all, q)
        } catch (e) {
            done(String(e && e.message || e), [])
            return
        }
        done("", list)
    }

    srcs = [searchOpenMeteo(q, get), searchPhoton(raw, get)].map(function (p) {
        var s = { state: 0, list: [], err: null }   // state：0 还在查 / 1 成功 / 2 失败
        p.then(function (list) { s.state = 1; s.list = list; check() },
               function (e) { s.state = 2; s.err = e instanceof Error ? e : new Error(String(e)); check() })
        return s
    })
    function check() {
        if (srcs.length > 0 && srcs.every(function (s) { return s.state !== 0 }))
            finish()
    }
    return { expire: finish, cancel: stop }
}

function rank(all, q) {
    all.forEach(function (r, i) { r.match = matchTier(r.name, q); r.idx = i })
    // 同 Windows 版 OrderBy(Match).ThenBy(Level).ThenByDescending(Pop).ThenBy(Order)；LINQ 是稳定排序，最后按原位置排
    all.sort(function (a, b) {
        return a.match - b.match || a.level - b.level || b.pop - a.pop || a.order - b.order || a.idx - b.idx
    })
    var res = []
    for (var i = 0; i < all.length && res.length < MAX; i++) {
        var r = all[i]
        if (res.some(function (k) {
            return k.name === r.name && (k.detail === r.detail || km(k, r) < 30) || km(k, r) < 8 && samePlaceName(k, r)
        }))
            continue
        res.push(r)
    }
    return res.map(function (r) { return { name: r.name, detail: r.detail, lat: r.lat, lon: r.lon } })
}

// 名字和搜索词对得上的程度：0 = 一样（或只差"市""区""县"等后缀），1 = 以它开头，2 = 其他（地名库的模糊匹配）。
// 搜的是拼音 / 英文、给回来的是中文（日文、韩文）名，说明是按别名对上的（"Tokyo" → 东京，"wuhan" → 武汉），也算 0
function matchTier(n, q) {
    var un = n.toUpperCase(), uq = q.toUpperCase()   // 同 StringComparison.OrdinalIgnoreCase
    if (un === uq)
        return 0
    if (!zh.hasCjk(q) && !hasLatin(n))
        return 0
    for (var i = 0; i < AdminSuffix.length; i++)
        if (n === q + AdminSuffix[i] || q === n + AdminSuffix[i])
            return 0
    return un.indexOf(uq) === 0 ? 1 : 2
}

// 有没有拉丁字母（同 C# 的 c < 0x250 && char.IsLetter(c)）
function hasLatin(s) {
    for (var i = 0; i < s.length; i++) {
        var c = s.charCodeAt(i)
        if (c >= 0x41 && c <= 0x5A || c >= 0x61 && c <= 0x7A || c === 0xAA || c === 0xB5 || c === 0xBA
                || c >= 0xC0 && c <= 0x24F && c !== 0xD7 && c !== 0xF7)
            return true
    }
    return false
}

// 相距很近、名字不同的两条是不是同一个地方：去掉"市""区""街道"等后缀后一个包含另一个（"武汉"/"武汉市"、"景洪市"/"景洪农场"），
// 或者一个是中文、一个不是（两个库给的写法不同，"巴黎"/"Paris"）——这种只认两条都是市、区县一级往上的（城市边上的公园、村子不并进城市）：
// 村子、山峰这类小地方两个库的写法对不齐，跨写法还同名的太多，并错了真地点就丢了，宁可贵一点留着两条。
// 相邻的不同区县（"汉阳区"/"汉口"）不算
function samePlaceName(a, b) {
    if (zh.hasCjk(a.name) !== zh.hasCjk(b.name))
        return Math.max(a.level, b.level) <= 2
    var x = stem(a.name).toUpperCase(), y = stem(b.name).toUpperCase()
    return x.indexOf(y) >= 0 || y.indexOf(x) >= 0
}

// 去掉最长的行政后缀（剩下至少两个字才去）："景洪市" → "景洪"，"陆家嘴街道" → "陆家嘴"
function stem(n) {
    var best = ""
    AdminSuffix.forEach(function (s) {
        if (n.endsWith(s) && n.length - s.length >= 2 && s.length > best.length)
            best = s
    })
    return n.substring(0, n.length - best.length)
}

function km(a, b) {
    var k = Math.PI / 180, x = (b.lon - a.lon) * k * Math.cos((a.lat + b.lat) / 2 * k), y = (b.lat - a.lat) * k
    return Math.sqrt(x * x + y * y) * 6371
}

function str(v) { return v === undefined || v === null ? "" : String(v) }

// OpenStreetMap 的名字常带当地其他文字（"景洪市 ᦵᦋᧂ…""香港 Hong Kong"）：前半段是中文就只留它。繁体转成简体
function cleanName(s) {
    s = str(s).trim()
    var sp = s.indexOf(" ")
    return zh.simplified(sp > 0 && zh.hasCjk(s.substring(0, sp)) ? s.substring(0, sp) : s)
}

function joinDetail(name, parts) {
    var list = []
    parts.map(cleanName).forEach(function (p) {
        if (p !== "" && p !== name && list.indexOf(p) < 0)
            list.push(p)
    })
    return list.join(", ")
}

// GeoNames 的中文名简繁混杂，很多外国城市只有繁体（"纽约""伦敦""西雅图"搜不到，"紐約""倫敦""西雅圖"能搜到）：
// 搜索词里有能转繁体的字，就简体、繁体各查一次。
// 两个字的词它只认完全一样的名字，而不少大城市记的是带"市"的（"罗马市""紐約市""大阪市"）：再带上"市"各查一次
function searchOpenMeteo(name, get) {
    var names = [name, zh.traditional(name)]
    if (name.length === 2 && zh.isCjk(name[0]) && zh.isCjk(name[1]))
        names = names.concat(names.map(function (n) { return n + "市" }))
    return openMeteoQuery(names, get).then(function (list) {
        // GeoNames 里很多区县没有带后缀的写法（"武汉市""余杭区"搜不到，"武汉""余杭"能搜到）：没结果就去掉后缀再查一次
        if (list.length === 0 && name.length > 2 && "市区县".indexOf(name[name.length - 1]) >= 0) {
            var s = name.substring(0, name.length - 1)
            return openMeteoQuery([s, zh.traditional(s)], get)
        }
        return list
    })
}

// 同时查几种写法（重复的只查一次），按写法的顺序合在一起；都失败才算失败
function openMeteoQuery(names, get) {
    var uniq = names.filter(function (n, i) { return names.indexOf(n) === i })
    return Promise.all(uniq.map(function (n) {
        return get(OPEN_METEO + encodeURIComponent(n)).then(function (t) { return { text: t } }, function (e) { return { err: e } })
    })).then(function (rs) {
        var list = [], err = null, ok = false
        rs.forEach(function (r) {
            if (r.err) {
                err = err || r.err
                return
            }
            try {
                parseOpenMeteo(r.text, list)
                ok = true
            } catch (e) {
                err = err || e
            }
        })
        if (!ok)
            throw err
        return list
    })
}

function parseOpenMeteo(json, list) {
    var d = JSON.parse(json)
    var arr = d && Array.isArray(d.results) ? d.results : []
    arr.forEach(function (r) {
        if (!r || r.latitude === undefined || r.latitude === null || r.longitude === undefined || r.longitude === null)
            return
        var code = str(r.feature_code)
        var level = code === "PPLC" || code.startsWith("PCL") ? 0
                  : code === "PPLA" || code === "PPLA2" || code === "ADM1" ? 1
                  : code === "PPLA3" || code === "ADM2" || code === "ADM3" ? 2
                  : code === "PPLA4" || code.startsWith("ADM") ? 3
                  : code.startsWith("PPL") ? 5 : 4
        var pop = r.population !== undefined && r.population !== null ? Number(r.population) || 0 : 0
        // GeoNames 把不少大城市记成普通居民点（纽约是 PPL）：按人口往前提
        level = Math.min(level, pop >= 1000000 ? 0 : pop >= 100000 ? 1 : pop >= 10000 ? 3 : 9)
        var n = zh.simplified(str(r.name))
        if (n.trim() === "")
            return   // 没名字的条目：空串和什么都"匹配"，会排到前面、把真结果当重复吞掉（Photon 那边同样跳过）
        list.push({ name: n, detail: joinDetail(n, [r.admin2, r.admin1, r.country]),
                    lat: Number(r.latitude), lon: Number(r.longitude), level: level, pop: pop, order: list.length })
    })
}

function searchPhoton(name, get) {
    // Photon 按用户输入的原文查：OSM 里台湾、港澳的地名是繁体（「信義區」），转成简体反而查不到；Open-Meteo 自己会简繁各查一遍
    return get(PHOTON + encodeURIComponent(name), "zh").then(function (json) {
        var d = JSON.parse(json), list = []
        var arr = d && Array.isArray(d.features) ? d.features : []
        arr.forEach(function (f) {
            var p = f && f.properties, g = f && f.geometry, xy = g && g.coordinates
            if (!p || typeof p !== "object" || !Array.isArray(xy) || xy.length < 2)
                return
            var key = str(p.osm_key), val = str(p.osm_value), type = str(p.type), level
            if (key === "place") {
                switch (val) {
                case "country": level = 0; break
                case "state": case "province": case "region": case "city": case "municipality": level = 1; break
                case "county": case "district": case "borough": level = 2; break
                case "town": level = 3; break
                case "suburb": case "island": level = 4; break
                case "village": level = 5; break
                case "hamlet": case "quarter": case "neighbourhood": case "locality": case "islet": level = 6; break
                default: return   // 广场、农场、单独的房子等
                }
            } else if (key === "boundary") {
                level = type === "state" ? 1 : type === "county" || type === "city" ? 2 : type === "district" ? 4 : 6
            } else {
                level = 4   // 山峰
            }
            var n = cleanName(p.name)
            if (n === "")
                return
            list.push({ name: n, detail: joinDetail(n, [p.county, p.city, p.state, p.country]),
                        lat: Number(xy[1]), lon: Number(xy[0]), level: level, pop: 0, order: list.length })
        })
        return list
    })
}

// 按经纬度反查到的地名（BigDataCloud）拼成"城市 · 区县"：同 Windows 版 WeatherService.LookupName，
// 它的中文名简繁混着给（"武汉市 · 江岸區"），和搜城市一样转成简体；没有城市名时只用区县（别拼成" · 江岸区"）
function placeName(d) {
    var city = zh.simplified(str(d && d.city)), locality = zh.simplified(str(d && d.locality))
    if (city === "")
        city = zh.simplified(str(d && d.principalSubdivision))
    if (locality !== "" && locality !== city)
        return city === "" ? locality : city + " · " + locality
    return city
}
