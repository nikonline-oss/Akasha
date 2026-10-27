namespace Akasha.Server;

/// <summary>Статическая страница входа владельца (без Blazor-контура, работает до аутентификации).</summary>
public static class LoginPage
{
    private sealed record Strings(
        string Lang, string Title, string Subtitle, string EmailPlaceholder, string PasswordPlaceholder,
        string Submit, string LoginError, string BadCredentials, string NoServer, string SwitchTo);

    private static readonly Strings Ru = new(
        "ru", "Вход — Akasha", "Вход для владельца хранилища", "Email", "Пароль",
        "Войти", "Ошибка входа", "Неверный email или пароль", "Нет связи с сервером", "English");

    private static readonly Strings En = new(
        "en", "Sign in — Akasha", "Owner sign-in", "Email", "Password",
        "Sign in", "Sign-in failed", "Wrong email or password", "No connection to the server", "Русский");

    /// <summary>Язык: ?lang=ru|en, иначе Accept-Language (en* → EN), иначе RU.</summary>
    public static string ResolveLang(HttpContext ctx)
    {
        var q = ctx.Request.Query["lang"].ToString();
        if (q is "ru" or "en") return q;
        var accept = ctx.Request.Headers.AcceptLanguage.ToString();
        if (!string.IsNullOrEmpty(accept) && accept.TrimStart().StartsWith("en", StringComparison.OrdinalIgnoreCase))
            return "en";
        return "ru";
    }

    public static string Html(string? lang)
    {
        var s = lang == "en" ? En : Ru;
        var other = s.Lang == "en" ? "ru" : "en";
        return $$"""
<!doctype html>
<html lang="{{s.Lang}}">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>{{s.Title}}</title>
<style>
 body{font-family:system-ui,Segoe UI,Roboto,sans-serif;background:#1A0B2E;color:#fff;display:flex;align-items:center;justify-content:center;min-height:100vh;margin:0}
 .card{background:#fff;color:#2A0845;border-radius:12px;padding:32px;width:340px;box-shadow:0 20px 60px rgba(0,0,0,.4);position:relative}
 h1{margin:0 0 8px;font-size:20px}
 p{color:#777;margin:0 0 20px;font-size:13px}
 input{width:100%;box-sizing:border-box;padding:12px;margin:8px 0;border:1px solid #ddd;border-radius:8px;font-size:14px}
 button{width:100%;padding:12px;background:#6C3FC5;color:#fff;border:0;border-radius:8px;font-size:14px;cursor:pointer;margin-top:8px}
 button:hover{background:#5a33ab}
 .err{color:#B3261E;font-size:13px;margin-top:10px;min-height:18px}
 .lang{position:absolute;top:12px;right:16px;font-size:12px;color:#6C3FC5;text-decoration:none}
 .lang:hover{text-decoration:underline}
</style>
</head>
<body>
<form class="card" id="f">
  <a class="lang" href="?lang={{other}}">{{s.SwitchTo}}</a>
  <h1>✦ Akasha</h1>
  <p>{{s.Subtitle}}</p>
  <input name="email" type="email" placeholder="{{s.EmailPlaceholder}}" required autocomplete="username">
  <input name="password" type="password" placeholder="{{s.PasswordPlaceholder}}" required autocomplete="current-password">
  <button type="submit">{{s.Submit}}</button>
  <div class="err" id="err"></div>
</form>
<script>
const f=document.getElementById('f'),e=document.getElementById('err');
f.addEventListener('submit',async(ev)=>{
  ev.preventDefault();
  e.textContent='';
  try{
    const r=await fetch('/api/v1/auth/login',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({email:f.email.value,password:f.password.value})});
    if(r.ok){location.href='/';return;}
    let m='{{s.LoginError}}';
    try{const j=await r.json();m=j.error||m;}catch(_){}
    e.textContent=(r.status===401?'{{s.BadCredentials}}':m);
  }catch(_){e.textContent='{{s.NoServer}}';}
});
</script>
</body>
</html>
""";
    }
}
