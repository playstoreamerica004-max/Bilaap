(() => {
  const $ = id => document.getElementById(id);
  let token = null, currentUser = null, selectedPerson = null, connection = null;
  const authView = $("authView"), chatView = $("chatView");
  const setStatus = (message, error=false) => { $("authStatus").textContent=message; $("authStatus").classList.toggle("error",error); };
  const phoneValue = () => $("phone").value.trim();
  async function api(url, options={}) {
    const headers = {"Content-Type":"application/json", ...(options.headers||{})};
    if (token) headers.Authorization = `Bearer ${token}`;
    const res = await fetch(url,{...options,headers});
    const data = await res.json().catch(()=>({}));
    if (!res.ok) throw new Error(data.error || data.detail || "وقع خطأ. حاول مرة أخرى.");
    return data;
  }
  $("sendCode").addEventListener("click", async () => {
    setStatus("كنطلبو إرسال رمز التحقق...");
    $("sendCode").disabled=true;
    try {
      const data=await api("/api/auth/start",{method:"POST",body:JSON.stringify({phone:phoneValue()})});
      $("verifyArea").classList.remove("hidden");
      setStatus(data.message || "راجع رسائل SMS.");
    } catch(e){setStatus(e.message,true);}
    finally{$("sendCode").disabled=false;}
  });
  $("verifyCode").addEventListener("click", async () => {
    setStatus("كنتحققو من الرمز...");
    $("verifyCode").disabled=true;
    try {
      const data=await api("/api/auth/verify",{method:"POST",body:JSON.stringify({phone:phoneValue(),code:$("code").value.trim()})});
      token=data.token; currentUser=data.user;
      authView.classList.add("hidden"); chatView.classList.remove("hidden");
      await loadPeople(); await connectHub();
    } catch(e){setStatus(e.message,true);}
    finally{$("verifyCode").disabled=false;}
  });
  async function loadPeople(){
    const people=await api("/api/users");
    $("people").replaceChildren();
    if(!people.length){$("people").innerHTML='<p class="muted small">مازال ما كاين حتى مستخدم آخر. سجّل رقم ثاني للاختبار.</p>';return;}
    people.forEach(person=>{
      const button=document.createElement("button");button.className="person";
      const avatar=document.createElement("span");avatar.className="avatar";avatar.textContent="ب";
      const info=document.createElement("span");info.className="person-info";
      const name=document.createElement("strong");name.textContent=person.displayName;
      const phone=document.createElement("span");phone.textContent=person.phone;
      info.append(name,phone);button.append(avatar,info);
      button.addEventListener("click",()=>openConversation(person,button));
      $("people").append(button);
    });
  }
  async function connectHub(){
    if(!window.signalR){setStatus("تعذر تحميل SignalR. تحقق من اتصال الإنترنت.");return;}
    connection=new signalR.HubConnectionBuilder().withUrl("/chatHub",{accessTokenFactory:()=>token}).withAutomaticReconnect().build();
    connection.on("ReceiveMessage", message=>{if(selectedPerson && (message.senderId===selectedPerson.id || message.recipientId===selectedPerson.id)) addMessage(message);});
    try{await connection.start();}catch(e){console.error(e);alert("تعذر الاتصال بالمحادثات المباشرة. تأكد من تشغيل الخادم والإنترنت.");}
  }
  async function openConversation(person,button){
    selectedPerson=person;
    document.querySelectorAll(".person").forEach(el=>el.classList.remove("selected"));button.classList.add("selected");
    $("emptyChat").classList.add("hidden");$("activeChat").classList.remove("hidden");$("chatName").textContent=person.displayName;$("messages").replaceChildren();
    try{const messages=await api(`/api/messages/${person.id}`);messages.forEach(addMessage);$("messages").scrollTop=$("messages").scrollHeight;}
    catch(e){alert(e.message);}
  }
  function addMessage(m){
    const el=document.createElement("div");el.className="bubble"+(m.senderId===currentUser.id?" mine":"");el.dataset.id=m.id;
    const text=document.createElement("div");text.textContent=m.text;
    const time=document.createElement("time");time.textContent=new Date(m.sentUtc).toLocaleTimeString("ar-MA",{hour:"2-digit",minute:"2-digit"});
    el.append(text,time);
    if(!$("messages").querySelector(`[data-id="${m.id}"]`)){$("messages").append(el);$("messages").scrollTop=$("messages").scrollHeight;}
  }
  $("sendForm").addEventListener("submit",async e=>{
    e.preventDefault();const text=$("messageInput").value.trim();if(!text||!selectedPerson||!connection)return;
    $("messageInput").value="";
    try{await connection.invoke("SendMessage",selectedPerson.id,text);}
    catch(err){alert("ما قدرناش نصيفطو الرسالة: "+err.message);$("messageInput").value=text;}
  });
  $("logout").addEventListener("click",async()=>{if(connection)await connection.stop();token=null;currentUser=null;selectedPerson=null;chatView.classList.add("hidden");authView.classList.remove("hidden");$("verifyArea").classList.add("hidden");$("code").value="";setStatus("");});
})(); 
