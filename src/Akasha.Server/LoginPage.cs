namespace Akasha.Server;

/// <summary>Статическая страница входа владельца (без Blazor-контура, работает до аутентификации).</summary>
public static class LoginPage
{
    public const string Html = """
<!doctype html>
<html lang="ru">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Вход — Akasha</title>
<style>
 body{font-family:system-ui,Segoe UI,Roboto,sans-serif;background:#1A0B2E;color:#fff;display:flex;align-items:center;justify-content:center;min-height:100vh;margin:0}
 .card{background:#fff;color:#2A0845;border-radius:12px;padding:32px;width:340px;box-shadow:0 20px 60px rgba(0,0,0,.4)}
 h1{margin:0 0 8px;font-size:20px}
 p{color:#777;margin:0 0 20px;font-size:13px}
 input{width:100%;box-sizing:border-box;padding:12px;margin:8px 0;border:1px solid #ddd;border-radius:8px;font-size:14px}
 button{width:100%;padding:12px;background:#6C3FC5;color:#fff;border:0;border-radius:8px;font-size:14px;cursor:pointer;margin-top:8px}
 button:hover{background:#5a33ab}
 .err{color:#B3261E;font-size:13px;margin-top:10px;min-height:18px}
</style>
</head>
<body>
<form class="card" id="f">
  <h1>✦ Akasha</h1>
  <p>Вход для владельца хранилища</p>
  <input name="email" type="email" placeholder="Email" required autocomplete="username">
  <input name="password" type="password" placeholder="Пароль" required autocomplete="current-password">
  <button type="submit">Войти</button>
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
    let m='Ошибка входа';
    try{const j=await r.json();m=j.error||m;}catch(_){}
    e.textContent=(r.status===401?'Неверный email или пароль':m);
  }catch(_){e.textContent='Нет связи с сервером';}
});
</script>
</body>
</html>
""";
}
