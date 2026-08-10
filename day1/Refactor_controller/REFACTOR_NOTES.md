everything in one function: Everything is happening in one function from validation to lookup to price logic makes function inefficient and hard to read
controller directly talks to db: controller directly talks to db , no testing can be done should build a repository/service layer
catch blocks: all catch are empty and after each one even when they throw the code still will build success=true response which can lead to critical functionality bugs
FirstOrDefault(...) and SaveChanges() : they are called synchronously even in an async function can block the thread pool and no point in writing async then.
getOrder : it hits db but is not sync again thread pool blocking
product lookup : it is one per item which will make it very slow it can be batched.
No DTOs: whenever working we should build DTOs so that we can fix in what format data should be sent and recieved.eliminates unecessary IFs
wrong loop bounds off by one count
null deref: in address.city and in category.name
console.writeline used for logging instead of a logger
no unit or integrated tests